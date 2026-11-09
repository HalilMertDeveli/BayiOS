import 'dart:convert';
import 'dart:typed_data';

import 'package:bayios_mobile/core/api/api_client.dart';
import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/features/picking/picking_api.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:dio/dio.dart';

import '../../helpers/fakes.dart';

const userId = 'user-1';
const otherUserId = 'user-2';
const tenantId = 't1';

/// A line of the fake task (mutable server state).
class FakeLine {
  FakeLine({
    required this.id,
    required this.sequence,
    required this.sku,
    required this.barcode,
    required this.toPick,
    this.locationId,
    this.locationCode,
    this.locationBarcode,
    this.name = 'Ürün',
    this.unit = 'adet',
  });

  final String id;
  final int sequence;
  final String sku;
  final String barcode;
  // Mutable: a re-allocation (#98) moves the line to another shelf.
  String? locationId;
  String? locationCode;
  String? locationBarcode;

  /// `locationIsActive` (#98); null = field absent (older API).
  bool? locationIsActive;
  final String name;
  final String unit;
  final double toPick;
  double picked = 0;
  double short = 0;
  String? shortageReason;

  double get remaining => toPick - picked - short;

  Map<String, Object?> toJson() => {
    'id': id,
    'sequence': sequence,
    'orderLineId': 'ol-$id',
    'variantId': 'var-$sku',
    'sku': sku,
    'name': name,
    'unit': unit,
    'barcode': barcode,
    'locationId': locationId,
    'locationCode': locationCode,
    'locationBarcode': locationBarcode,
    // Decimal-safe wire format: quantities may arrive as strings.
    'quantityToPick': '$toPick',
    'quantityPicked': picked,
    'quantityShort': short,
    'quantityRemaining': remaining,
    'shortageReason': shortageReason,
    if (locationIsActive != null) 'locationIsActive': locationIsActive,
  };
}

/// A shelf with free stock a scan may re-allocate a line to (#98).
class FakeShelf {
  const FakeShelf(this.id, this.code, this.barcode);

  final String id;
  final String code;
  final String barcode;
}

/// A fake Warehouse API for one task: validates scans like PickingService
/// (wrong item / shelf, over-pick), bumps the version, honours If-Match and
/// replays a stored 2xx for a repeated Idempotency-Key (24 h store).
class FakePickingServer implements HttpClientAdapter {
  FakePickingServer({
    this.assignedTo = userId,
    this.status = 'open',
    List<FakeLine>? lines,
  }) : lines = lines ?? defaultLines();

  static List<FakeLine> defaultLines() => [
    FakeLine(
      id: 'line-1',
      sequence: 1,
      sku: 'FLT-001',
      barcode: '8690001',
      name: 'Yağ filtresi',
      toPick: 2,
      locationId: 'loc-a',
      locationCode: 'A-01-01',
      locationBarcode: 'LOC-A0101',
    ),
    FakeLine(
      id: 'line-2',
      sequence: 2,
      sku: 'BRK-002',
      barcode: '8690002',
      name: 'Fren balatası',
      toPick: 1,
      locationId: 'loc-b',
      locationCode: 'B-02-03',
      locationBarcode: 'LOC-B0203',
    ),
  ];

  String? assignedTo;
  String status;

  /// #98 task facts.
  String priority = 'normal';
  String? customerName = 'Yıldız Oto';
  String? requestedDeliveryDate = '2026-11-12';

  /// Shelves (not on the task) where a scan re-allocates the line.
  final freeShelves = <FakeShelf>[];
  final List<FakeLine> lines;
  int version = 3;
  String? shipmentId;

  /// Every request, in order.
  final requests = <RequestOptions>[];

  /// Stored responses by Idempotency-Key.
  final _replays = <String, FakeReply>{};

  /// Applied scans (a replay does not count again).
  int appliedScans = 0;

  /// One-shot overrides, consumed by the next matching request
  /// (`'POST /api/v1/picking-tasks/task-1/scans'` → reply).
  final overrides = <String, List<FakeReply Function()>>{};

  /// The next write is processed but its answer is lost (connection drop).
  bool dropNextAnswer = false;

  /// `/mine` and list responses.
  List<Map<String, Object?>> Function()? mineItems;
  List<Map<String, Object?>> Function()? listItems;
  bool listHasMore = false;

  /// Extra pages of `GET /picking-tasks?cursor=…`.
  List<Map<String, Object?>> Function(String cursor)? listPage;

  static const taskPath = '/api/v1/picking-tasks/task-1';

  Map<String, Object?> summaryJson() => {
    'id': 'task-1',
    'type': 'pick',
    'status': status,
    'orderId': 'order-1',
    'orderNumber': 'SO-1001',
    'warehouseId': 'wh-1',
    'assignedTo': assignedTo,
    'assigneeName': assignedTo == null
        ? null
        : assignedTo == userId
        ? 'Ayşe Depocu'
        : 'Mehmet Toplayıcı',
    'lineCount': lines.length,
    'quantityToPick': lines.fold<double>(0, (a, l) => a + l.toPick),
    'quantityPicked': lines.fold<double>(0, (a, l) => a + l.picked),
    'quantityShort': lines.fold<double>(0, (a, l) => a + l.short),
    'createdAt': '2026-11-08T08:00:00Z',
    'startedAt': null,
    'completedAt': null,
    'version': '$version',
    'priority': priority,
    'customerName': customerName,
    'requestedDeliveryDate': requestedDeliveryDate,
  };

  Map<String, Object?> taskJson() => {
    'task': summaryJson(),
    'lines': [for (final l in lines) l.toJson()],
    'shipmentId': shipmentId,
  };

  List<RequestOptions> writes(String suffix) => [
    for (final r in requests)
      if (r.method == 'POST' && r.path.endsWith(suffix)) r,
  ];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    final route = '${options.method} ${options.path}';
    final queued = overrides[route];
    if (queued != null && queued.isNotEmpty) {
      return _body(queued.removeAt(0)(), options);
    }

    final key = options.headers[ApiClient.idempotencyHeader] as String?;
    if (key != null && _replays.containsKey(key)) {
      return _body(_replays[key]!, options, replayed: true);
    }
    final reply = _handle(options);
    if (key != null && reply.status >= 200 && reply.status < 300) {
      _replays[key] = reply;
    }
    if (key != null && dropNextAnswer) {
      dropNextAnswer = false;
      throw DioException(
        requestOptions: options,
        type: DioExceptionType.connectionError,
      );
    }
    return _body(reply, options);
  }

  ResponseBody _body(
    FakeReply reply,
    RequestOptions options, {
    bool replayed = false,
  }) {
    final error = reply.error;
    if (error != null) {
      throw DioException(requestOptions: options, type: error);
    }
    return ResponseBody.fromString(
      reply.body,
      reply.status,
      headers: {
        ...reply.headers,
        if (replayed) 'idempotent-replayed': ['true'],
      },
    );
  }

  static FakeReply problem(
    int status,
    String code, {
    Map<String, List<String>>? errors,
    Map<String, String> headers = const {},
  }) => FakeReply(
    status,
    json: {
      'type': 'about:blank',
      'title': code,
      'status': status,
      'code': code,
      'errors': ?errors,
    },
    headers: headers,
  );

  FakeReply _ok(Map<String, Object?> json) =>
      FakeReply(200, json: json, headers: {'etag': 'W/"$version"'});

  FakeReply _handle(RequestOptions o) {
    final path = o.path;
    if (o.method == 'GET' && path == '/api/v1/picking-tasks/mine') {
      return FakeReply(
        200,
        json: {
          'items': mineItems?.call() ?? [summaryJson()],
        },
      );
    }
    if (o.method == 'GET' && path == '/api/v1/picking-tasks') {
      final cursor = o.queryParameters['cursor'] as String?;
      final items = cursor != null && listPage != null
          ? listPage!(cursor)
          : listItems?.call() ?? [summaryJson()];
      return FakeReply(
        200,
        json: {
          'items': items,
          'page': {
            'nextCursor': listHasMore && cursor == null ? 'c2' : null,
            'hasMore': listHasMore && cursor == null,
            'limit': 50,
          },
        },
      );
    }
    if (o.method == 'GET' && path == '/api/v1/orders/order-1') {
      return FakeReply(
        200,
        json: {
          'order': {
            'number': 'SO-1001',
            'status': 'approved',
            'customerName': null,
            'warehouseName': 'Merkez Depo',
            'requestedDeliveryDate': '2026-11-10',
            'notes': 'Öğlene kadar',
            'hasShortage': false,
          },
          'lines': <Object>[],
          'history': <Object>[],
          'shipments': <Object>[],
        },
      );
    }
    if (o.method == 'GET' && path == taskPath) return _ok(taskJson());
    if (o.method != 'POST' || !path.startsWith(taskPath)) {
      return problem(404, 'resource.not_found');
    }

    final ifMatch = o.headers['If-Match'] as String?;
    if (ifMatch != null && ifMatch != 'W/"$version"') {
      return problem(412, 'concurrency.version_mismatch');
    }
    final body = o.data is String
        ? jsonDecode(o.data as String) as Map<String, Object?>
        : const <String, Object?>{};

    if (path == '$taskPath/claim') {
      if (assignedTo != null && assignedTo != userId) {
        return problem(409, 'picking.already_assigned');
      }
      assignedTo = userId;
      version++;
      return _ok(taskJson());
    }
    if (path == '$taskPath/scans') return _scan(body);
    if (path == '$taskPath/complete') {
      if (lines.any((l) => l.remaining > 0)) {
        return problem(409, 'picking.incomplete');
      }
      status = lines.any((l) => l.short > 0)
          ? 'completed_with_shortage'
          : 'completed';
      shipmentId = 'ship-1';
      version++;
      return _ok(taskJson());
    }
    final shortage = RegExp(r'/lines/([^/]+)/shortage$').firstMatch(path);
    if (shortage != null) {
      final line = lines.firstWhere((l) => l.id == shortage.group(1));
      final quantity = (body['quantity']! as num).toDouble();
      if (quantity > line.remaining) {
        return problem(422, 'picking.shortage_exceeds_open');
      }
      line
        ..short += quantity
        ..shortageReason = body['reason'] as String?;
      status = 'in_progress';
      version++;
      return _ok(taskJson());
    }
    return problem(404, 'resource.not_found');
  }

  FakeReply _scan(Map<String, Object?> body) {
    final code = body['code']! as String;
    final location = body['location'] as String?;
    final quantity = (body['quantity'] as num?)?.toDouble() ?? 1;
    final candidates = lines
        .where(
          (l) => l.barcode == code || l.sku.toUpperCase() == code.toUpperCase(),
        )
        .toList();
    if (candidates.isEmpty) {
      return code.startsWith('869')
          ? problem(422, 'picking.wrong_item')
          : problem(422, 'picking.unknown_code');
    }
    if (location == null) return problem(422, 'picking.location_required');
    // Re-allocation (#98): another shelf with free stock takes the line.
    final free = freeShelves
        .where((f) => f.barcode == location || f.code == location.toUpperCase())
        .firstOrNull;
    if (free != null) {
      candidates.first
        ..locationId = free.id
        ..locationCode = free.code
        ..locationBarcode = free.barcode;
    }
    final known = lines.any(
      (l) =>
          l.locationBarcode == location ||
          l.locationCode == location.toUpperCase(),
    );
    if (!known && !location.startsWith('LOC-')) {
      return problem(422, 'picking.unknown_location');
    }
    final onShelf = candidates
        .where(
          (l) =>
              l.locationBarcode == location ||
              l.locationCode == location.toUpperCase(),
        )
        .toList();
    if (onShelf.isEmpty) return problem(422, 'picking.wrong_location');
    final line = onShelf.first;
    if (quantity > line.remaining) return problem(422, 'picking.over_pick');
    line.picked += quantity;
    appliedScans++;
    status = 'in_progress';
    version++;
    return FakeReply(
      200,
      json: {
        'taskId': 'task-1',
        'status': status,
        'version': version,
        'lines': [
          {
            'id': line.id,
            'variantId': 'var-${line.sku}',
            'sku': line.sku,
            'locationId': line.locationId,
            'quantityToPick': line.toPick,
            'quantityPicked': line.picked,
            'quantityShort': line.short,
            'quantityRemaining': line.remaining,
          },
        ],
        'openLineCount': lines.where((l) => l.remaining > 0).length,
      },
      headers: {'etag': 'W/"$version"'},
    );
  }

  @override
  void close({bool force = false}) {}
}

/// [PickingApi] over [server] (real ApiClient: headers, Problem Details).
PickingApi pickingApiFor(FakePickingServer server) => PickingApi(
  ApiClient(
    dio: Dio(BaseOptions(baseUrl: 'http://api.test'))
      ..httpClientAdapter = server,
    tokens: FakeAuthRepository(
      user: const AuthUser(id: userId, email: 'ayse@ornek.test'),
    ),
    activeTenantId: () => tenantId,
    clientVersion: '0.1.0',
    clientPlatform: 'android',
  ),
);

/// Records the haptic/sound alerts.
class RecordingAlerts implements ScanAlerts {
  final events = <String>[];

  @override
  void accepted() => events.add('accepted');

  @override
  void rejected() => events.add('rejected');
}
