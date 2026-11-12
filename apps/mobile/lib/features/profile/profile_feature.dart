import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/features/profile/profile_screen.dart';

/// Profil — account, tenant switch, sign-out (M10).
final profileFeature = AppFeature(
  id: 'profile',
  path: '/profil',
  label: (l10n) => l10n.navProfile,
  icon: AppIcons.profile,
  selectedIcon: AppIcons.profile,
  builder: (context, state) => const ProfileScreen(),
);
