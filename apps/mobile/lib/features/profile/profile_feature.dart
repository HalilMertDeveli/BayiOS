import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/features/profile/profile_screen.dart';
import 'package:flutter/material.dart';

/// Profil — account, tenant switch, sign-out (M10).
final profileFeature = AppFeature(
  id: 'profile',
  path: '/profil',
  label: (l10n) => l10n.navProfile,
  icon: Icons.person_outline,
  selectedIcon: Icons.person,
  builder: (context, state) => const ProfileScreen(),
);
