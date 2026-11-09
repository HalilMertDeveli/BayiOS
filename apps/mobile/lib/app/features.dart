import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/features/dashboard/dashboard_feature.dart';
import 'package:bayios_mobile/features/picking/picking_feature.dart';
import 'package:bayios_mobile/features/profile/profile_feature.dart';
import 'package:bayios_mobile/features/stock/stock_feature.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_feature.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Every feature of the signed-in app, in bottom-navigation order.
/// Adding a feature = one line here (see lib/app/feature.dart).
final List<AppFeature> appFeatures = [
  dashboardFeature,
  pickingFeature,
  stockFeature,
  warehouseFeature,
  profileFeature,
];

/// Overridable in tests.
final appFeaturesProvider = Provider<List<AppFeature>>((ref) => appFeatures);
