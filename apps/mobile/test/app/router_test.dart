import 'package:bayios_mobile/app/feature.dart';
import 'package:bayios_mobile/app/router.dart';
import 'package:bayios_mobile/app/shell.dart';
import 'package:bayios_mobile/core/api/api_exception.dart';
import 'package:bayios_mobile/core/session/access.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:bayios_mobile/core/session/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../helpers/fakes.dart';

AppFeature _feature(String id, {bool Function(Access)? visible}) => AppFeature(
  id: id,
  path: '/$id',
  label: (_) => id,
  icon: Icons.circle,
  builder: (_, _) => const SizedBox(),
  isVisible: visible ?? (_) => true,
);

SessionReady _ready(List<String> permissions) {
  final me = meFixture(
    memberships: [membershipJson('t1')],
    activeTenant: activeTenantJson('t1', permissions: permissions),
  );
  return SessionReady(
    me: me,
    memberships: me.memberships,
    membership: me.memberships.single,
    activeTenant: me.activeTenant!,
  );
}

void main() {
  final features = [
    _feature('pano'),
    _feature('gorevler', visible: (a) => a.canSome('tasks.pick')),
    _feature('profil'),
  ];

  test('gate screens follow the session state', () {
    expect(
      redirectFor(const SessionSignedOut(), '/pano', features),
      AppRoutes.signIn,
    );
    expect(
      redirectFor(const SessionSignedOut(), AppRoutes.signIn, features),
      isNull,
    );
    expect(
      redirectFor(const SessionLoading(), '/pano', features),
      AppRoutes.loading,
    );
    expect(
      redirectFor(SessionFailed(ApiException.network()), '/x', features),
      AppRoutes.loading,
    );
    final me = meFixture(memberships: [membershipJson('t1')]);
    expect(
      redirectFor(SessionNeedsTenant(me, me.memberships), '/pano', features),
      AppRoutes.tenantPicker,
    );
    expect(
      redirectFor(SessionNoMembership(me), '/pano', features),
      AppRoutes.noMembership,
    );
  });

  test('ready: gate screens go home, deep links stay', () {
    final ready = _ready(['tenant.read', 'tasks.pick']);
    expect(redirectFor(ready, AppRoutes.signIn, features), '/pano');
    expect(redirectFor(ready, AppRoutes.tenantPicker, features), '/pano');
    expect(redirectFor(ready, '/gorevler', features), isNull);
    expect(redirectFor(ready, '/gorevler/abc', features), isNull);
    expect(redirectFor(ready, '/bilinmeyen', features), '/pano');
  });

  test('a feature hidden by permissions cannot be opened', () {
    final ready = _ready(['tenant.read']);
    expect(redirectFor(ready, '/gorevler', features), '/pano');
    expect(visibleFeatures(features, ready.access).map((f) => f.id), [
      'pano',
      'profil',
    ]);
    expect(
      visibleFeatures(features, _ready(['tasks.pick']).access).map((f) => f.id),
      ['pano', 'gorevler', 'profil'],
    );
  });

  test('access: scopes', () {
    final access = Access([
      const PermissionGrant(permission: 'a', scope: PermissionScope.all),
      const PermissionGrant(
        permission: 'b',
        scope: PermissionScope.own,
        condition: 'orders.own',
      ),
    ]);
    expect(access.can('a'), isTrue);
    expect(access.can('b'), isFalse);
    expect(access.canSome('b'), isTrue);
    expect(access.condition('b'), 'orders.own');
    expect(access.canAny(['x', 'b']), isTrue);
    expect(Access.none.canSome('a'), isFalse);
  });
}
