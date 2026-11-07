/// Build-time configuration, chosen per environment with `--dart-define` or
/// `--dart-define-from-file=env/<env>.json` (git-ignored; see
/// env/dev.example.json). Nothing secret lives here: the Supabase anon /
/// publishable key is public by design (ADR 0003 §6). Never put a
/// service-role key, a connection string or any other secret in a define.
enum AppEnvironment {
  dev,
  preview,
  prod;

  static AppEnvironment? parse(String value) {
    for (final env in values) {
      if (env.name == value) return env;
    }
    return null;
  }
}

class AppEnv {
  const AppEnv({
    required this.environment,
    required this.apiBaseUrl,
    required this.supabaseUrl,
    required this.supabasePublishableKey,
    required this.clientVersion,
  });

  /// Reads the compile-time defines. Returns the problems found instead of
  /// throwing so the app can show a readable configuration screen.
  static ({AppEnv? env, List<String> problems}) fromDefines() => validate(
    environment: const String.fromEnvironment('BAYIOS_ENV'),
    apiBaseUrl: const String.fromEnvironment('BAYIOS_API_BASE_URL'),
    supabaseUrl: const String.fromEnvironment('BAYIOS_SUPABASE_URL'),
    supabasePublishableKey: const String.fromEnvironment(
      'BAYIOS_SUPABASE_PUBLISHABLE_KEY',
    ),
    clientVersion: const String.fromEnvironment(
      'BAYIOS_CLIENT_VERSION',
      defaultValue: '0.1.0',
    ),
  );

  static ({AppEnv? env, List<String> problems}) validate({
    required String environment,
    required String apiBaseUrl,
    required String supabaseUrl,
    required String supabasePublishableKey,
    required String clientVersion,
  }) {
    final problems = <String>[];
    final env = AppEnvironment.parse(environment.isEmpty ? 'dev' : environment);
    if (env == null) problems.add('BAYIOS_ENV must be dev, preview or prod');

    Uri? checkUrl(String name, String value) {
      final uri = Uri.tryParse(value.trim());
      if (value.trim().isEmpty || uri == null || !uri.hasAuthority) {
        problems.add('$name is missing or not an absolute URL');
        return null;
      }
      // Only dev builds may talk plain HTTP (local stack, emulator 10.0.2.2).
      if (env != AppEnvironment.dev && uri.scheme != 'https') {
        problems.add('$name must use https outside dev');
      }
      return uri;
    }

    final api = checkUrl('BAYIOS_API_BASE_URL', apiBaseUrl);
    final supabase = checkUrl('BAYIOS_SUPABASE_URL', supabaseUrl);
    if (supabasePublishableKey.trim().isEmpty) {
      problems.add('BAYIOS_SUPABASE_PUBLISHABLE_KEY is missing');
    }
    if (problems.isNotEmpty || env == null || api == null || supabase == null) {
      return (env: null, problems: problems);
    }
    return (
      env: AppEnv(
        environment: env,
        apiBaseUrl: _stripTrailingSlash(api.toString()),
        supabaseUrl: _stripTrailingSlash(supabase.toString()),
        supabasePublishableKey: supabasePublishableKey.trim(),
        clientVersion: clientVersion,
      ),
      problems: const <String>[],
    );
  }

  final AppEnvironment environment;

  /// API origin without `/api/v1`, e.g. `https://api.bayios.app`.
  final String apiBaseUrl;
  final String supabaseUrl;
  final String supabasePublishableKey;

  /// Sent as `X-Client-Version` (api-strategy §4).
  final String clientVersion;

  static String _stripTrailingSlash(String value) =>
      value.replaceFirst(RegExp(r'/+$'), '');
}
