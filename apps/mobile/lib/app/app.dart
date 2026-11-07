import 'package:bayios_mobile/app/router.dart';
import 'package:bayios_mobile/core/providers.dart';
import 'package:bayios_mobile/core/ui/app_theme.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

const appLocale = Locale('tr');

class BayiOsApp extends ConsumerWidget {
  const BayiOsApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return MaterialApp.router(
      onGenerateTitle: (context) => AppLocalizations.of(context).appTitle,
      debugShowCheckedModeBanner: false,
      theme: buildAppTheme(),
      locale: appLocale,
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      routerConfig: ref.watch(routerProvider),
      builder: (context, child) =>
          WithOfflineBanner(child: child ?? const SizedBox.shrink()),
    );
  }
}

/// Puts the [OfflineBanner] above every screen while the API is unreachable.
class WithOfflineBanner extends ConsumerWidget {
  const WithOfflineBanner({required this.child, super.key});

  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final reachable = ref.watch(networkStatusProvider);
    if (reachable) return child;
    return Column(
      children: [
        const OfflineBanner(),
        // The banner already consumed the status-bar inset.
        Expanded(
          child: MediaQuery.removePadding(
            context: context,
            removeTop: true,
            child: child,
          ),
        ),
      ],
    );
  }
}

/// Shown instead of the app when the build lacks its `--dart-define`s.
class ConfigErrorApp extends StatelessWidget {
  const ConfigErrorApp({required this.problems, super.key});

  final List<String> problems;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildAppTheme(),
      locale: appLocale,
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      home: Builder(
        builder: (context) {
          final l10n = AppLocalizations.of(context);
          return Scaffold(
            body: SafeArea(
              child: ListView(
                padding: const EdgeInsets.all(24),
                children: [
                  Text(
                    l10n.configErrorTitle,
                    style: Theme.of(context).textTheme.headlineSmall,
                  ),
                  const SizedBox(height: 12),
                  Text(l10n.configErrorBody),
                  const SizedBox(height: 12),
                  for (final p in problems) Text('• $p'),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}
