import 'package:intl/intl.dart';

/// The API takes quantities > 0 with at most 3 decimals
/// (≤ 999 999 999 999 999), the same rule as the web (`quantity.ts`).
const maxQuantity = 999999999999999;
const maxQuantityDecimals = 3;

/// Why a typed quantity was rejected (message chosen by the screen).
enum QuantityError { empty, invalid, tooManyDecimals, notPositive, tooLarge }

/// Result of [parseQuantity]: either [value] or [error].
class QuantityParse {
  const QuantityParse.ok(num this.value) : error = null;
  const QuantityParse.failed(QuantityError this.error) : value = null;

  final num? value;
  final QuantityError? error;

  bool get ok => error == null;
}

final _quantityPattern = RegExp(r'^\d+([.,]\d+)?$');

/// Parses a quantity typed the Turkish way (`12,5`) or with a dot (`12.5`).
/// Thousands separators are rejected: `1.250` would be ambiguous, so the only
/// separator allowed is the decimal one.
QuantityParse parseQuantity(String input) {
  final text = input.trim().replaceAll(RegExp(r'\s+'), '');
  if (text.isEmpty) return const QuantityParse.failed(QuantityError.empty);
  if (!_quantityPattern.hasMatch(text)) {
    return const QuantityParse.failed(QuantityError.invalid);
  }
  final normalized = text.replaceAll(',', '.');
  final dot = normalized.indexOf('.');
  final decimals = dot < 0 ? 0 : normalized.length - dot - 1;
  if (decimals > maxQuantityDecimals) {
    return const QuantityParse.failed(QuantityError.tooManyDecimals);
  }
  final value = decimals == 0 ? int.parse(normalized) : num.parse(normalized);
  if (value <= 0) return const QuantityParse.failed(QuantityError.notPositive);
  if (value > maxQuantity) {
    return const QuantityParse.failed(QuantityError.tooLarge);
  }
  return QuantityParse.ok(value);
}

/// Adds two quantities without binary floating-point noise (3 decimals).
num addQuantities(num a, num b) {
  final sum = ((a + b) * 1000).round() / 1000;
  return sum == sum.truncate() ? sum.toInt() : sum;
}

/// A quantity the API sent as a JSON number or a numeric string
/// (`format: double` with a string pattern in the contract).
num readQuantity(Object? value) => switch (value) {
  final num n => n,
  final String s => num.tryParse(s) ?? 0,
  _ => 0,
};

num? readOptionalQuantity(Object? value) => switch (value) {
  final num n => n,
  final String s => num.tryParse(s),
  _ => null,
};

final _format = NumberFormat('#,##0.###', 'tr');

/// Display form (`1.250,5`).
String formatQuantity(num value) => _format.format(value);

/// Back into the input's text form (`12,5`).
String quantityText(num value) =>
    (value == value.truncate() ? value.toInt() : value).toString().replaceAll(
      '.',
      ',',
    );
