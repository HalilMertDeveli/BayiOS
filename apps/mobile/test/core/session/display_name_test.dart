import 'package:bayios_mobile/core/auth/auth_repository.dart';
import 'package:bayios_mobile/core/auth/supabase_auth_repository.dart';
import 'package:bayios_mobile/core/session/display_name.dart';
import 'package:bayios_mobile/core/session/me.dart';
import 'package:flutter_test/flutter_test.dart';

Me _me({String? fullName, String? email = 'ayse.depo@ornek.test'}) =>
    Me.fromJson({
      'userId': 'u1',
      'email': email,
      'fullName': fullName,
      'memberships': <Object>[],
      'activeTenant': null,
    });

void main() {
  test('GET /me fullName wins', () {
    expect(
      displayNameFor(
        _me(fullName: ' Ayşe Depocu '),
        const AuthUser(id: 'u1', fullName: 'Başka'),
      ),
      'Ayşe Depocu',
    );
  });

  test('then Supabase user_metadata.full_name (as the web)', () {
    expect(
      displayNameFor(_me(), const AuthUser(id: 'u1', fullName: 'Mehmet')),
      'Mehmet',
    );
  });

  test('then the local part of the e-mail address', () {
    expect(displayNameFor(_me(fullName: '  '), null), 'ayse.depo');
    expect(
      displayNameFor(
        _me(email: null),
        const AuthUser(id: 'u1', email: 'depo1@firma.test'),
      ),
      'depo1',
    );
  });

  test('nothing usable → null (anonymous greeting)', () {
    expect(displayNameFor(_me(email: null), null), isNull);
  });

  test('metadataFullName reads only non-blank strings', () {
    expect(metadataFullName({'full_name': ' Ayşe '}), 'Ayşe');
    expect(metadataFullName({'full_name': ''}), isNull);
    expect(metadataFullName({'full_name': 42}), isNull);
    expect(metadataFullName(null), isNull);
  });
}
