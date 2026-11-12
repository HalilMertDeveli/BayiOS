import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_screen.dart';

/// Pano — warehouse dashboard: my open tasks, counts and quick links.
/// Every member can see it (`tenant.read` is granted to all roles).
final dashboardFeature = AppFeature(
  id: 'dashboard',
  path: '/pano',
  label: (l10n) => l10n.navDashboard,
  icon: AppIcons.dashboard1,
  selectedIcon: AppIcons.dashboard1,
  builder: (context, state) => const DashboardScreen(),
);
