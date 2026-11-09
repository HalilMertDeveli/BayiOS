import 'dart:convert';
import 'dart:io';

import 'package:bayios_mobile/app/features.dart';
import 'package:bayios_mobile/core/api/contract.dart';
import 'package:bayios_mobile/core/api/endpoint.dart';
import 'package:bayios_mobile/core/session/identity_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:flutter_test/flutter_test.dart';

/// Contract drift check (ADR 0007 / ADR 0011): every endpoint and model field
/// the app uses must match packages/contracts/openapi.json. CI runs this on
/// every change to apps/mobile/** or the contract.
void main() {
  final file = File('../../packages/contracts/openapi.json');
  final spec = jsonDecode(file.readAsStringSync()) as Map<String, Object?>;
  final paths = spec['paths']! as Map<String, Object?>;
  final schemas =
      (spec['components']! as Map<String, Object?>)['schemas']!
          as Map<String, Object?>;

  final endpoints = <ApiEndpoint>{
    ...CoreEndpoints.all,
    for (final f in appFeatures) ...f.endpoints,
  };
  final models = <ContractSchema>{
    ...CoreEndpoints.schemas,
    for (final f in appFeatures) ...f.schemas,
  };

  group('endpoints', () {
    for (final endpoint in endpoints) {
      test('$endpoint', () {
        final item = paths[endpoint.path] as Map<String, Object?>?;
        expect(item, isNotNull, reason: 'path not in the contract');
        final op = item![endpoint.method.name] as Map<String, Object?>?;
        expect(op, isNotNull, reason: 'method not in the contract');

        final headers = {
          for (final p in (op!['parameters'] as List<Object?>? ?? const []))
            if (p is Map<String, Object?> && p['in'] == 'header')
              p['name']! as String: p['required'] == true,
        };
        switch (endpoint.tenant) {
          case TenantScope.required:
            expect(headers['X-Tenant-Id'], isTrue, reason: 'X-Tenant-Id');
          case TenantScope.optional:
            expect(headers['X-Tenant-Id'], isFalse, reason: 'X-Tenant-Id');
          case TenantScope.none:
            expect(headers.containsKey('X-Tenant-Id'), isFalse);
        }
        expect(
          headers['Idempotency-Key'] ?? false,
          endpoint.idempotent,
          reason: 'Idempotency-Key required ⇔ endpoint.idempotent',
        );
        final secured = (op['security'] as List<Object?>?)?.isNotEmpty ?? false;
        expect(secured, endpoint.authenticated, reason: 'Bearer security');
      });
    }
  });

  // Screens gate on these permission ids; the contract's x-permission(s)
  // must grant them. If the contract stops granting a relocation with
  // inventory.transfer (#98), this fails: update WarehousePermissions.relocate.
  group('permissions', () {
    final gates = <ApiEndpoint, String>{
      WarehouseEndpoints.receive: WarehousePermissions.receiveTransfer,
      WarehouseEndpoints.transfers: WarehousePermissions.receiveTransfer,
      WarehouseEndpoints.transfer: WarehousePermissions.receiveTransfer,
      WarehouseEndpoints.countEntries: WarehousePermissions.performCount,
      WarehouseEndpoints.countSubmit: WarehousePermissions.performCount,
      WarehouseEndpoints.counts: WarehousePermissions.performCount,
      WarehouseEndpoints.count: WarehousePermissions.performCount,
      WarehouseEndpoints.relocation: WarehousePermissions.relocate,
    };
    for (final MapEntry(key: endpoint, value: permission) in gates.entries) {
      test('$endpoint ⇐ $permission', () {
        final item = paths[endpoint.path]! as Map<String, Object?>;
        final op = item[endpoint.method.name]! as Map<String, Object?>;
        final declared = <String>{
          if (op['x-permission'] case final String p) p,
          ...?(op['x-permission-any-of'] as List<Object?>?)?.cast<String>(),
        };
        expect(declared, contains(permission));
      });
    }
  });

  // #98: a refused shelf move (409) carries `shortages` (StockShortage[]);
  // the relocation screen shows how much can still move.
  test('POST /inventory/relocations 409 is a StockProblemDetails', () {
    final op =
        (paths['/api/v1/inventory/relocations']!
                as Map<String, Object?>)['post']!
            as Map<String, Object?>;
    final conflict =
        (op['responses']! as Map<String, Object?>)['409']!
            as Map<String, Object?>;
    final ref =
        (((conflict['content']!
                        as Map<String, Object?>)['application/problem+json']!
                    as Map<String, Object?>)['schema']!
                as Map<String, Object?>)[r'$ref']!
            as String;
    expect(ref.split('/').last, 'StockProblemDetails');
    final parts =
        (schemas['StockProblemDetails']! as Map<String, Object?>)['allOf']!
            as List<Object?>;
    final shortages = [
      for (final part in parts.cast<Map<String, Object?>>())
        if ((part['properties'] as Map<String, Object?>?)?['shortages']
            case final Map<String, Object?> s)
          s,
    ].single;
    expect(
      (shortages['items']! as Map<String, Object?>)[r'$ref'],
      '#/components/schemas/StockShortage',
    );
  });

  group('schemas', () {
    for (final model in models) {
      test(model.name, () {
        final schema = schemas[model.name] as Map<String, Object?>?;
        expect(schema, isNotNull, reason: 'schema not in the contract');
        final props = schema!['properties']! as Map<String, Object?>;
        final required = {
          ...?(schema['required'] as List<Object?>?)?.cast<String>(),
        };
        for (final entry in model.fields.entries) {
          final field = entry.key;
          final nullableInModel = entry.value.endsWith('?');
          final type = entry.value.replaceAll('?', '');
          final prop = props[field] as Map<String, Object?>?;
          expect(prop, isNotNull, reason: '${model.name}.$field missing');

          final (types, nullable) = _types(prop!, schemas);
          expect(
            types,
            contains(type),
            reason: '${model.name}.$field: contract types $types, model $type',
          );
          if (!nullableInModel) {
            expect(
              nullable,
              isFalse,
              reason: '${model.name}.$field may be null; mark it "$type?"',
            );
            expect(
              required,
              contains(field),
              reason: '${model.name}.$field is optional; mark it "$type?"',
            );
          }
        }
      });
    }
  });
}

/// JSON types a property allows and whether null is one of them
/// (`type: [null, string]`, `oneOf: [{type: null}, {$ref}]`, `$ref`).
(Set<String>, bool) _types(
  Map<String, Object?> prop,
  Map<String, Object?> schemas,
) {
  final types = <String>{};
  var nullable = false;
  void add(Map<String, Object?> p) {
    if (p[r'$ref'] case final String ref) {
      final target = schemas[ref.split('/').last] as Map<String, Object?>?;
      if (target == null) return;
      if (target.containsKey('type') || target.containsKey('oneOf')) {
        add(target);
      } else {
        types.add('object');
      }
      return;
    }
    switch (p['type']) {
      case final String t:
        types.add(t);
      case final List<Object?> ts:
        for (final t in ts.whereType<String>()) {
          if (t == 'null') {
            nullable = true;
          } else {
            types.add(t);
          }
        }
    }
    if (p['type'] == 'null') nullable = true;
    for (final key in ['oneOf', 'anyOf', 'allOf']) {
      for (final sub in (p[key] as List<Object?>? ?? const [])) {
        if (sub is Map<String, Object?>) add(sub);
      }
    }
    if (p.containsKey('enum') && types.isEmpty) types.add('string');
  }

  add(prop);
  types.remove('null');
  return (types, nullable);
}
