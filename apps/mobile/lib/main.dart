import 'package:bayios_mobile/app/app.dart';
import 'package:bayios_mobile/app/env.dart';
import 'package:bayios_mobile/core/auth/auth_controller.dart';
import 'package:bayios_mobile/core/auth/secure_session_storage.dart';
import 'package:bayios_mobile/core/auth/supabase_auth_repository.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:supabase_flutter/supabase_flutter.dart';

/// Run with the environment file (git-ignored, see env/dev.example.json):
/// `flutter run --dart-define-from-file=env/dev.json`
Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  final config = AppEnv.fromDefines();
  final env = config.env;
  if (env == null) {
    runApp(ConfigErrorApp(problems: config.problems));
    return;
  }

  // Supabase is used for the auth session only: no table, storage or
  // realtime access from the app (CLAUDE.md — data goes through the API).
  await Supabase.initialize(
    url: env.supabaseUrl,
    publishableKey: env.supabasePublishableKey,
    authOptions: FlutterAuthClientOptions(
      localStorage: SecureSessionStorage(),
      pkceAsyncStorage: const SecureGotrueAsyncStorage(),
      // Password sign-in only: no deep-link callbacks to handle.
      detectSessionInUri: false,
    ),
  );

  runApp(
    ProviderScope(
      // Retries are explicit (ErrorView "Tekrar dene", IdempotentAction).
      retry: (retryCount, error) => null,
      overrides: [
        appEnvProvider.overrideWithValue(env),
        authRepositoryProvider.overrideWithValue(
          SupabaseAuthRepository(Supabase.instance.client.auth),
        ),
      ],
      child: const BayiOsApp(),
    ),
  );
}
