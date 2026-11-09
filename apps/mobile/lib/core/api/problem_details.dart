/// RFC 9457 Problem Details as the API returns them (api-strategy §6).
///
/// `code` is the stable localisation key; `title` / `detail` are English
/// developer aids and are never shown to users. Extension members (e.g.
/// `shortages`, `permission`, `feature`) stay available in [extensions].
class ProblemDetails {
  const ProblemDetails({
    this.type,
    this.title,
    this.status,
    this.detail,
    this.instance,
    this.code,
    this.correlationId,
    this.errors = const {},
    this.extensions = const {},
  });

  /// Tolerant parser: unknown or mistyped members never throw.
  factory ProblemDetails.fromJson(Map<String, Object?> json) {
    String? str(String key) {
      final value = json[key];
      return value is String && value.isNotEmpty ? value : null;
    }

    final rawStatus = json['status'];
    final status = rawStatus is int
        ? rawStatus
        : rawStatus is String
        ? int.tryParse(rawStatus)
        : null;

    final errors = <String, List<String>>{};
    final rawErrors = json['errors'];
    if (rawErrors is Map<String, Object?>) {
      for (final entry in rawErrors.entries) {
        final value = entry.value;
        if (value is List<Object?>) {
          errors[entry.key] = value.whereType<String>().toList();
        } else if (value is String) {
          errors[entry.key] = [value];
        }
      }
    }

    const known = {
      'type',
      'title',
      'status',
      'detail',
      'instance',
      'code',
      'correlationId',
      'errors',
    };
    return ProblemDetails(
      type: str('type'),
      title: str('title'),
      status: status,
      detail: str('detail'),
      instance: str('instance'),
      code: str('code'),
      correlationId: str('correlationId'),
      errors: errors,
      extensions: {
        for (final entry in json.entries)
          if (!known.contains(entry.key)) entry.key: entry.value,
      },
    );
  }

  final String? type;
  final String? title;
  final int? status;
  final String? detail;
  final String? instance;
  final String? code;
  final String? correlationId;

  /// Field errors of `422 validation.failed`.
  final Map<String, List<String>> errors;
  final Map<String, Object?> extensions;

  /// A string extension member (e.g. `permission`), or null.
  String? extensionString(String name) {
    final value = extensions[name];
    return value is String && value.isNotEmpty ? value : null;
  }
}
