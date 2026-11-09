import 'package:bayios_mobile/features/picking/picking_models.dart';

/// Pure state of the pick screen, ported from the web
/// (apps/web/src/lib/fulfilment/pick-session.ts): which line is current,
/// which shelf was scanned and how a scanned code is read. The API stays the
/// judge of every scan (wrong item, wrong shelf, over-pick); this only
/// decides what to send and what to show.

/// Lines in shelf pick order (the API sorts by location; `sequence` keeps it
/// stable).
List<PickingTaskLine> sortLines(Iterable<PickingTaskLine> lines) =>
    [...lines]..sort((a, b) => a.sequence.compareTo(b.sequence));

/// The preferred line while it is still open, else the first open line.
PickingTaskLine? currentLine(
  Iterable<PickingTaskLine> lines, [
  String? preferredId,
]) {
  final sorted = sortLines(lines);
  if (preferredId != null) {
    for (final l in sorted) {
      if (l.id == preferredId && l.isOpen) return l;
    }
  }
  for (final l in sorted) {
    if (l.isOpen) return l;
  }
  return null;
}

/// The warehouse tracks shelves when any line carries a location.
bool usesLocations(Iterable<PickingTaskLine> lines) =>
    lines.any((l) => l.locationId != null);

/// A scanned shelf.
class ScannedLocation {
  const ScannedLocation({
    required this.code,
    required this.label,
    this.locationId,
  });

  /// Sent as `location` (barcode or code exactly as scanned).
  final String code;

  /// The task's location it matches; null when the shelf is not on this
  /// task (the API decides on the item scan).
  final String? locationId;

  /// Location code when known, else what was scanned.
  final String label;

  bool get isKnown => locationId != null;
}

bool _same(String? a, String b) =>
    a != null && a.toUpperCase() == b.toUpperCase();

/// A shelf barcode / code matched against the task's lines.
ScannedLocation? matchLocation(String code, Iterable<PickingTaskLine> lines) {
  for (final l in lines) {
    if (l.locationId != null &&
        (l.locationBarcode == code || _same(l.locationCode, code))) {
      return ScannedLocation(
        code: code,
        locationId: l.locationId,
        label: l.locationCode ?? code,
      );
    }
  }
  return null;
}

/// Lines whose product the code is (barcode exact, SKU case-insensitive like
/// the API).
List<PickingTaskLine> matchItem(String code, Iterable<PickingTaskLine> lines) =>
    sortLines(lines.where((l) => l.barcode == code || _same(l.sku, code)));

sealed class ScanReading {
  const ScanReading();
}

final class LocationReading extends ScanReading {
  const LocationReading(this.location);

  final ScannedLocation location;
}

final class ItemReading extends ScanReading {
  const ItemReading(this.code);

  final String code;
}

/// How a scan is read:
/// 1. a product of this task → item;
/// 2. a shelf of this task → shelf;
/// 3. an unknown code in a shelf-tracked warehouse while no shelf is set →
///    taken as the shelf (the API answers `picking.unknown_location` /
///    `wrong_location` on the item scan); once a shelf is set it is sent as
///    an item (`picking.unknown_code` / `wrong_item`);
/// 4. otherwise → item.
ScanReading? readScan(
  String raw,
  Iterable<PickingTaskLine> lines,
  ScannedLocation? location,
) {
  final code = raw.trim();
  if (code.isEmpty) return null;
  if (matchItem(code, lines).isNotEmpty) return ItemReading(code);
  final shelf = matchLocation(code, lines);
  if (shelf != null) return LocationReading(shelf);
  if (usesLocations(lines) && location == null) {
    return LocationReading(ScannedLocation(code: code, label: code));
  }
  return ItemReading(code);
}

enum PickStep { done, location, item }

PickStep pickStep(PickingTaskLine? current, ScannedLocation? location) {
  if (current == null) return PickStep.done;
  if (current.locationId != null && location == null) return PickStep.location;
  return PickStep.item;
}

/// Keep the scanned shelf only while the next line is on it.
ScannedLocation? keepLocation(
  ScannedLocation? location,
  PickingTaskLine? next,
) {
  if (location == null || next == null || next.locationId == null) {
    return null;
  }
  if (!location.isKnown) return location;
  return location.locationId == next.locationId ? location : null;
}

/// After a rejection a wrong or unknown shelf is forgotten (scan the right
/// one); everything else stays.
ScannedLocation? locationAfterRejection(
  String? code,
  ScannedLocation? location,
) =>
    code == PickingProblem.wrongLocation ||
        code == PickingProblem.unknownLocation
    ? null
    : location;

/// Problem codes of the picking endpoints (PickingService.cs).
abstract final class PickingProblem {
  static const unknownCode = 'picking.unknown_code';
  static const wrongItem = 'picking.wrong_item';
  static const locationRequired = 'picking.location_required';
  static const unknownLocation = 'picking.unknown_location';
  static const wrongLocation = 'picking.wrong_location';
  static const overPick = 'picking.over_pick';
  static const alreadyAssigned = 'picking.already_assigned';
  static const notAssignee = 'picking.not_assignee';
  static const invalidTransition = 'picking.invalid_transition';
  static const orderNotFulfillable = 'picking.order_not_fulfillable';
  static const lineClosed = 'picking.line_closed';
  static const shortageExceedsOpen = 'picking.shortage_exceeds_open';
  static const incomplete = 'picking.incomplete';
  static const busy = 'picking.busy';

  /// Rejected scans: nothing changed on the server; fix the scan, go on.
  static const Set<String> scanRejections = {
    unknownCode,
    wrongItem,
    locationRequired,
    unknownLocation,
    wrongLocation,
    overPick,
  };

  /// The view is stale: reload the task.
  static const Set<String> stale = {invalidTransition, lineClosed, incomplete};
}

/// Totals over the lines.
class PickTotals {
  const PickTotals({
    required this.toPick,
    required this.picked,
    required this.short,
    required this.openLines,
    required this.shortLines,
  });

  factory PickTotals.of(Iterable<PickingTaskLine> lines) {
    double add(double a, double b) => ((a + b) * 1000).roundToDouble() / 1000;
    var toPick = 0.0;
    var picked = 0.0;
    var short = 0.0;
    var open = 0;
    var shortLines = 0;
    for (final l in lines) {
      toPick = add(toPick, l.quantityToPick);
      picked = add(picked, l.quantityPicked);
      short = add(short, l.quantityShort);
      if (l.isOpen) open++;
      if (l.quantityShort > 0) shortLines++;
    }
    return PickTotals(
      toPick: toPick,
      picked: picked,
      short: short,
      openLines: open,
      shortLines: shortLines,
    );
  }

  final double toPick;
  final double picked;
  final double short;
  final int openLines;
  final int shortLines;

  double get progress =>
      toPick <= 0 ? 0 : ((picked + short) / toPick).clamp(0, 1);
}

/// Why a typed quantity is not accepted.
enum QuantityError { empty, invalid, notPositive, tooManyDecimals, overOpen }

/// A typed quantity (Turkish comma or dot as decimal separator).
sealed class QuantityParse {
  const QuantityParse();
}

final class QuantityOk extends QuantityParse {
  const QuantityOk(this.value);

  final double value;
}

final class QuantityInvalid extends QuantityParse {
  const QuantityInvalid(this.error);

  final QuantityError error;
}

/// Same rule as the API (`> 0`, at most 3 decimals) plus "not more than is
/// still open" when [max] is given, so the screen never invites an
/// over-pick. Whether the unit allows fractions is the API's decision.
QuantityParse parseQuantity(String text, {double? max}) {
  final trimmed = text.trim().replaceAll(',', '.');
  if (trimmed.isEmpty) return const QuantityInvalid(QuantityError.empty);
  if (!RegExp(r'^\d+(\.\d+)?$').hasMatch(trimmed)) {
    return const QuantityInvalid(QuantityError.invalid);
  }
  final dot = trimmed.indexOf('.');
  if (dot >= 0 && trimmed.length - dot - 1 > 3) {
    return const QuantityInvalid(QuantityError.tooManyDecimals);
  }
  final value = double.parse(trimmed);
  if (value <= 0) return const QuantityInvalid(QuantityError.notPositive);
  if (max != null && value > max + 1e-9) {
    return const QuantityInvalid(QuantityError.overOpen);
  }
  return QuantityOk(value);
}

/// `2`, `1,5`, `0,125` (Turkish decimal comma, no trailing zeros).
String formatQuantity(double value) {
  final rounded = (value * 1000).roundToDouble() / 1000;
  if (rounded == rounded.truncateToDouble()) {
    return rounded.toInt().toString();
  }
  var text = rounded.toStringAsFixed(3);
  while (text.endsWith('0')) {
    text = text.substring(0, text.length - 1);
  }
  return text.replaceAll('.', ',');
}
