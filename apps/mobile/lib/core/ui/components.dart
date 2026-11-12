import 'package:bayios_mobile/core/theme/app_colors.dart';
import 'package:bayios_mobile/core/theme/app_icon.dart';
import 'package:bayios_mobile/core/theme/app_spacing.dart';
import 'package:bayios_mobile/core/theme/app_theme.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';

/// The BayiOS mark (placeholder until the product logo exists): a brand
/// green tile with the store glyph. Decorative.
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 48});

  final double size;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return ExcludeSemantics(
      child: Container(
        width: size,
        height: size,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: [c.brandAccent, c.brandPrimary],
          ),
          borderRadius: BorderRadius.circular(size * 0.28),
        ),
        child: AppIcon(
          AppIcons.store03,
          size: size * 0.56,
          color: c.brandOnPrimary,
        ),
      ),
    );
  }
}

/// "BayiOS" wordmark: [BrandMark] plus the product name.
class BrandWordmark extends StatelessWidget {
  const BrandWordmark({super.key, this.markSize = 32});

  final double markSize;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        BrandMark(size: markSize),
        const SizedBox(width: AppSpacing.xs),
        Text(
          l10n.appTitle,
          style: context.text.titleLarge?.bold.copyWith(
            color: context.colors.textPrimary,
          ),
        ),
      ],
    );
  }
}

/// Icon on a tinted rounded square (quick actions, empty states, info rows),
/// or a circle ([round]).
class IconBadge extends StatelessWidget {
  const IconBadge({
    required this.icon,
    super.key,
    this.tone = Tone.brand,
    this.size = 40,
    this.round = false,
  });

  final AppIconData icon;
  final Tone tone;
  final double size;
  final bool round;

  @override
  Widget build(BuildContext context) {
    final t = context.colors.tone(tone);
    return SizedBox.square(
      dimension: size,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: t.bg,
          shape: round ? BoxShape.circle : BoxShape.rectangle,
          borderRadius: round ? null : BorderRadius.circular(size * 0.25),
        ),
        child: Center(
          child: AppIcon(icon, size: size * 0.5, color: t.fg),
        ),
      ),
    );
  }
}

/// Section title of a scrolling page ("Görevlerim", "Hızlı erişim").
class SectionHeader extends StatelessWidget {
  const SectionHeader(this.title, {super.key, this.trailing});

  final String title;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: AppSpacing.xs, bottom: AppSpacing.sm),
      child: Row(
        children: [
          Expanded(
            child: Semantics(
              header: true,
              child: Text(
                title,
                style: context.text.titleMedium?.copyWith(
                  color: context.colors.textPrimary,
                ),
              ),
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

/// Large bold page title under the top bar (kit "Settings", H5 24/34).
class PageTitle extends StatelessWidget {
  const PageTitle(this.title, {super.key, this.subtitle, this.trailing});

  final String title;
  final String? subtitle;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return Padding(
      padding: const EdgeInsets.only(bottom: AppSpacing.md),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Semantics(
                  header: true,
                  child: Text(
                    title,
                    style: context.text.headlineMedium?.copyWith(
                      color: c.textPrimary,
                    ),
                  ),
                ),
                if (subtitle case final subtitle?)
                  Text(
                    subtitle,
                    style: context.text.bodySmall?.copyWith(color: c.textMuted),
                  ),
              ],
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

/// Kit card: white (dark: white-5 %) surface, 1 px border, radius 16, no
/// heavy shadow. With [title] it becomes a section card (title row, hairline,
/// body). [onTap] makes the whole card one tap target.
class AppCard extends StatelessWidget {
  const AppCard({
    required this.child,
    super.key,
    this.title,
    this.trailing,
    this.padding = const EdgeInsets.all(AppSpacing.md),
    this.onTap,
    this.borderColor,
    this.color,
  });

  final Widget child;
  final String? title;
  final Widget? trailing;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;

  /// Highlight border (current line, selected item).
  final Color? borderColor;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    Widget body = Padding(padding: padding, child: child);
    if (title case final title?) {
      body = Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
              AppSpacing.md,
              AppSpacing.md,
              AppSpacing.md,
              AppSpacing.sm,
            ),
            child: Row(
              children: [
                Expanded(
                  child: Semantics(
                    header: true,
                    child: Text(
                      title,
                      style: context.text.titleLarge?.copyWith(
                        color: c.textPrimary,
                      ),
                    ),
                  ),
                ),
                ?trailing,
              ],
            ),
          ),
          const Divider(),
          body,
        ],
      );
    }
    return Card(
      color: color,
      clipBehavior: Clip.antiAlias,
      shape: RoundedRectangleBorder(
        borderRadius: AppRadius.lgAll,
        side: BorderSide(
          color: borderColor ?? c.borderDefault,
          width: borderColor == null ? 1 : 2,
        ),
      ),
      child: onTap == null ? body : InkWell(onTap: onTap, child: body),
    );
  }
}

/// KPI tile (kit "To be Shipped 212"): muted label, big number, tinted icon.
class KpiCard extends StatelessWidget {
  const KpiCard({
    required this.label,
    required this.value,
    required this.icon,
    super.key,
    this.tone = Tone.brand,
    this.onTap,
  });

  final String label;
  final String value;
  final AppIconData icon;
  final Tone tone;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return AppCard(
      onTap: onTap,
      padding: const EdgeInsets.all(AppSpacing.sm),
      child: Semantics(
        container: true,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ExcludeSemantics(
              child: IconBadge(icon: icon, tone: tone, size: 32),
            ),
            const SizedBox(height: AppSpacing.sm),
            // The number first for screen readers too: it is what the eye
            // looks for.
            Text(
              value,
              style: context.text.headlineMedium?.tabular.copyWith(
                color: c.textPrimary,
              ),
            ),
            Text(
              label,
              maxLines: 2,
              style: context.text.labelMedium?.copyWith(color: c.textMuted),
            ),
          ],
        ),
      ),
    );
  }
}

/// A row card of a list (the mobile replacement for a table row): leading
/// badge, title, subtitle lines, trailing widget and a chevron when tappable.
class ListCard extends StatelessWidget {
  const ListCard({
    required this.title,
    super.key,
    this.leading,
    this.subtitle,
    this.trailing,
    this.footer,
    this.onTap,
    this.enabled = true,
  });

  final Widget? leading;
  final String title;
  final String? subtitle;
  final Widget? trailing;

  /// Extra content under the title (chips, progress).
  final Widget? footer;
  final VoidCallback? onTap;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final tappable = onTap != null && enabled;
    return AppCard(
      onTap: tappable ? onTap : null,
      padding: const EdgeInsets.fromLTRB(
        AppSpacing.md,
        AppSpacing.sm,
        AppSpacing.xs,
        AppSpacing.sm,
      ),
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: AppSize.minTouchTarget),
        child: Row(
          children: [
            if (leading case final leading?) ...[
              leading,
              const SizedBox(width: AppSpacing.sm),
            ],
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    title,
                    style: context.text.titleMedium?.copyWith(
                      color: enabled ? c.textPrimary : c.textMuted,
                    ),
                  ),
                  if (subtitle case final subtitle?) ...[
                    const SizedBox(height: AppSpacing.xxs),
                    Text(
                      subtitle,
                      style: context.text.bodySmall?.copyWith(
                        color: c.textMuted,
                      ),
                    ),
                  ],
                  if (footer case final footer?) ...[
                    const SizedBox(height: AppSpacing.xs),
                    footer,
                  ],
                ],
              ),
            ),
            ?trailing,
            if (tappable)
              AppIcon(
                AppIcons.arrowRight2,
                size: AppSize.icon,
                color: c.textMuted,
              )
            else
              const SizedBox(width: AppSpacing.xs),
          ],
        ),
      ),
    );
  }
}

/// Small coloured label (status, "Tamam", "2 eksik"): tone background,
/// border and text, radius 6. Status is never colour-only: the text says it.
class StatusChip extends StatelessWidget {
  const StatusChip(this.text, this.tone, {super.key, this.icon});

  final String text;
  final Tone tone;

  /// Optional leading glyph.
  final AppIconData? icon;

  @override
  Widget build(BuildContext context) {
    final t = context.colors.tone(tone);
    return DecoratedBox(
      decoration: BoxDecoration(
        color: t.bg,
        border: Border.all(color: t.border),
        borderRadius: BorderRadius.circular(AppRadius.xs),
      ),
      child: Padding(
        padding: EdgeInsets.fromLTRB(
          icon == null ? AppSpacing.xs : AppSpacing.xxs + 2,
          AppSpacing.xxs - 1,
          AppSpacing.xs,
          AppSpacing.xxs - 1,
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon case final icon?) ...[
              AppIcon(icon, size: AppSize.iconSm, color: t.fg),
              const SizedBox(width: AppSpacing.xxs),
            ],
            Flexible(
              child: Text(
                text,
                style: context.text.labelMedium?.semiBold.copyWith(color: t.fg),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Up to two initials ("Ayşe Yılmaz" → "AY"; an e-mail → its first letter),
/// the same rule as the web (apps/web/src/lib/initials.ts). Words that do
/// not start with a letter or digit are skipped.
String initials(String name) {
  final parts = name
      .split(RegExp(r'[\s@._-]+'))
      .where((w) => w.isNotEmpty)
      .toList();
  final named = parts
      .where((w) => RegExp(r'^[\p{L}\p{N}]', unicode: true).hasMatch(w))
      .toList();
  final words = named.isNotEmpty ? named : parts;
  final letters = words.length > 1 && !name.contains('@')
      ? [words.first, words.last]
      : [if (words.isEmpty) '?' else words.first];
  return letters.map((w) => _upperTr(w.substring(0, 1))).join();
}

String _upperTr(String letter) => switch (letter) {
  'i' => 'İ',
  'ı' => 'I',
  _ => letter.toUpperCase(),
};

/// Round avatar with initials on a tone background (per-name, stable). Real
/// users never get photos (the API has none). Decorative: the name is shown
/// or announced next to it.
class AppAvatar extends StatelessWidget {
  const AppAvatar({required this.name, super.key, this.size = 40});

  final String name;
  final double size;

  static const List<Tone> _tones = [
    Tone.brand,
    Tone.info,
    Tone.purple,
    Tone.warning,
    Tone.success,
  ];

  static Tone toneFor(String name) {
    var hash = 0;
    for (final unit in name.codeUnits) {
      hash = (hash * 31 + unit) & 0xFFFFFFFF;
    }
    return _tones[hash % _tones.length];
  }

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final t = c.tone(toneFor(name));
    return ExcludeSemantics(
      child: Container(
        width: size,
        height: size,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: t.bg,
          shape: BoxShape.circle,
          border: Border.all(color: c.surfaceCard, width: 2),
        ),
        child: Text(
          initials(name),
          style:
              (size >= 48 ? context.text.titleMedium : context.text.titleSmall)
                  ?.copyWith(color: t.fg),
        ),
      ),
    );
  }
}

/// Kit header icon button: 40 dp bordered square (radius 8) inside a 48 dp
/// touch target.
class TopBarIconButton extends StatelessWidget {
  const TopBarIconButton({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
    super.key,
  });

  final AppIconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return IconButton(
      tooltip: tooltip,
      onPressed: onPressed,
      style: IconButton.styleFrom(
        minimumSize: const Size.square(AppSize.minTouchTarget),
        fixedSize: const Size.square(AppSize.minTouchTarget),
        padding: EdgeInsets.zero,
      ),
      icon: Container(
        width: AppSize.minTouchTarget - AppSpacing.xs,
        height: AppSize.minTouchTarget - AppSpacing.xs,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: c.surfaceCard,
          border: Border.all(color: c.borderDefault),
          borderRadius: AppRadius.smAll,
          boxShadow: AppShadows.hairline,
        ),
        child: AppIcon(icon, size: AppSize.icon, color: c.textPrimary),
      ),
    );
  }
}

/// The kit mobile header: white bar with a hairline, a bordered back button
/// on sub-pages, the page title, bordered action buttons and (on top-level
/// pages) the user's avatar on the right. An [AppBar] underneath, so the
/// system back, semantics and tests behave as before.
class AppTopBar extends StatelessWidget implements PreferredSizeWidget {
  const AppTopBar({
    required this.title,
    super.key,
    this.actions = const [],
    this.trailing,
    this.bottom,
    this.showBack,
    this.fitTitle = false,
  });

  final String title;
  final List<Widget> actions;

  /// Right-most widget (the user avatar on top-level pages).
  final Widget? trailing;
  final PreferredSizeWidget? bottom;

  /// Null: a back button whenever the route can pop.
  final bool? showBack;

  /// Shrink a long title instead of truncating it (order numbers the
  /// picker compares with the paper).
  final bool fitTitle;

  @override
  Size get preferredSize => Size.fromHeight(
    AppSize.topBarHeight + (bottom?.preferredSize.height ?? 0),
  );

  @override
  Widget build(BuildContext context) {
    final canPop =
        showBack ?? ModalRoute.of(context)?.impliesAppBarDismissal ?? false;
    return AppBar(
      automaticallyImplyLeading: false,
      leadingWidth: AppSize.minTouchTarget + AppSpacing.xs,
      leading: canPop
          ? Padding(
              padding: const EdgeInsets.only(left: AppSpacing.xs),
              child: TopBarIconButton(
                icon: AppIcons.arrowLeft2,
                tooltip: MaterialLocalizations.of(context).backButtonTooltip,
                onPressed: () => Navigator.maybePop(context),
              ),
            )
          : null,
      titleSpacing: canPop ? AppSpacing.xxs : AppSpacing.md,
      title: fitTitle
          ? FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(title),
            )
          : Text(title),
      bottom: bottom,
      actions: [
        ...actions,
        if (trailing case final trailing?) ...[
          const SizedBox(width: AppSpacing.xxs),
          trailing,
        ],
        const SizedBox(width: AppSpacing.sm),
      ],
    );
  }
}

/// One destination of [AppBottomNav].
@immutable
class AppNavItem {
  const AppNavItem({
    required this.key,
    required this.icon,
    required this.label,
    this.selectedIcon,
  });

  final Key key;
  final AppIconData icon;
  final AppIconData? selectedIcon;
  final String label;
}

/// Bottom navigation (the kit has none; derived): card surface, top hairline,
/// active = brand green icon + label, inactive = muted. A [NavigationBar]
/// underneath (semantics, 48 dp targets).
class AppBottomNav extends StatelessWidget {
  const AppBottomNav({
    required this.items,
    required this.selectedIndex,
    required this.onSelected,
    super.key,
  });

  final List<AppNavItem> items;
  final int selectedIndex;
  final ValueChanged<int> onSelected;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: c.surfaceCard,
        border: Border(top: BorderSide(color: c.borderDefault)),
      ),
      child: NavigationBar(
        selectedIndex: selectedIndex,
        onDestinationSelected: onSelected,
        destinations: [
          for (final item in items)
            NavigationDestination(
              key: item.key,
              icon: AppIcon(item.icon),
              selectedIcon: AppIcon(item.selectedIcon ?? item.icon),
              label: item.label,
            ),
        ],
      ),
    );
  }
}

//// Kit form field: the label above the input (not floating inside). The
/// label is also the field's accessible name; [trailing] (a scan / send
/// button) sits next to the field, outside its semantics.
class LabeledField extends StatelessWidget {
  const LabeledField({
    required this.label,
    required this.child,
    super.key,
    this.trailing,
    this.leading,
    this.center = false,
  });

  final String label;
  final Widget child;
  final Widget? leading;
  final Widget? trailing;

  /// Centred label (quantity steppers).
  final bool center;

  @override
  Widget build(BuildContext context) {
    final field = Semantics(label: label, child: child);
    return Column(
      crossAxisAlignment: center
          ? CrossAxisAlignment.center
          : CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        ExcludeSemantics(
          child: Text(
            label,
            style: context.text.bodySmall?.medium.copyWith(
              color: context.colors.textPrimary,
            ),
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        if (leading == null && trailing == null)
          field
        else
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (leading case final leading?) ...[
                leading,
                const SizedBox(width: AppSpacing.xs),
              ],
              Expanded(child: field),
              if (trailing case final trailing?) ...[
                const SizedBox(width: AppSpacing.xs),
                trailing,
              ],
            ],
          ),
      ],
    );
  }
}

//// Square − / + button of a quantity stepper: 56 dp, bordered, radius 8
/// (one-handed, glove-friendly).
class StepperButton extends StatelessWidget {
  const StepperButton({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
    super.key,
  });

  final AppIconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return IconButton(
      tooltip: tooltip,
      onPressed: onPressed,
      style: IconButton.styleFrom(
        fixedSize: const Size.square(AppSize.primaryAction),
        minimumSize: const Size.square(AppSize.primaryAction),
        backgroundColor: c.surfaceCard,
        foregroundColor: c.textPrimary,
        disabledForegroundColor: c.textMuted,
        disabledBackgroundColor: c.surfaceSunken,
        side: BorderSide(color: c.borderDefault),
        shape: const RoundedRectangleBorder(borderRadius: AppRadius.smAll),
      ),
      icon: AppIcon(icon, size: AppSize.iconMd),
    );
  }
}

/// Button variants of the kit.
enum AppButtonVariant { primary, secondary, tertiary, danger }

/// Kit button: primary (raised brand green), secondary (white, bordered),
/// tertiary (text), danger (solid red). [loading] shows a spinner, keeps the
/// width and blocks taps; the spinner is announced with [label].
class AppButton extends StatelessWidget {
  const AppButton({
    required this.label,
    required this.onPressed,
    super.key,
    this.variant = AppButtonVariant.primary,
    this.icon,
    this.loading = false,
    this.expand = false,
    this.loadingLabel,
    this.height = AppSize.minTouchTarget,
  });

  final String label;
  final VoidCallback? onPressed;
  final AppButtonVariant variant;
  final AppIconData? icon;
  final bool loading;
  final bool expand;
  final String? loadingLabel;
  final double height;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final size = Size(expand ? double.infinity : 0, height);
    final onPressed = loading ? null : this.onPressed;
    final child = loading
        ? Semantics(
            label: loadingLabel ?? label,
            child: const SizedBox.square(
              dimension: AppSize.icon,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
          )
        : Text(label);
    final iconWidget = icon == null || loading ? null : AppIcon(icon!);
    switch (variant) {
      case AppButtonVariant.primary:
        final style = FilledButton.styleFrom(minimumSize: size);
        return iconWidget == null
            ? FilledButton(onPressed: onPressed, style: style, child: child)
            : FilledButton.icon(
                onPressed: onPressed,
                style: style,
                icon: iconWidget,
                label: child,
              );
      case AppButtonVariant.danger:
        final style = FilledButton.styleFrom(
          minimumSize: size,
          backgroundColor: c.dangerSolid,
          foregroundColor: c.textInverse,
        ).copyWith(backgroundBuilder: (context, states, child) => child!);
        return iconWidget == null
            ? FilledButton(onPressed: onPressed, style: style, child: child)
            : FilledButton.icon(
                onPressed: onPressed,
                style: style,
                icon: iconWidget,
                label: child,
              );
      case AppButtonVariant.secondary:
        final style = OutlinedButton.styleFrom(minimumSize: size);
        return iconWidget == null
            ? OutlinedButton(onPressed: onPressed, style: style, child: child)
            : OutlinedButton.icon(
                onPressed: onPressed,
                style: style,
                icon: iconWidget,
                label: child,
              );
      case AppButtonVariant.tertiary:
        final style = TextButton.styleFrom(minimumSize: size);
        return iconWidget == null
            ? TextButton(onPressed: onPressed, style: style, child: child)
            : TextButton.icon(
                onPressed: onPressed,
                style: style,
                icon: iconWidget,
                label: child,
              );
    }
  }
}

/// The one empty / informational state of the app: a round tinted icon, a
/// title and what to do next; optionally an action.
class EmptyState extends StatelessWidget {
  const EmptyState({
    required this.icon,
    required this.title,
    super.key,
    this.body,
    this.tone = Tone.brand,
    this.action,
    this.compact = false,
  });

  final AppIconData icon;
  final String title;
  final String? body;
  final Tone tone;
  final Widget? action;

  /// Less vertical padding (inside a form, under a search field).
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return Padding(
      padding: EdgeInsets.symmetric(
        horizontal: AppSpacing.lg,
        vertical: compact ? AppSpacing.lg : AppSpacing.xxl,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ExcludeSemantics(
            child: _Halo(
              tone: tone,
              size: compact ? 64 : 80,
              child: IconBadge(
                icon: icon,
                tone: tone,
                size: compact ? 48 : 56,
                round: true,
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          Semantics(
            liveRegion: true,
            child: Text(
              title,
              textAlign: TextAlign.center,
              style: context.text.titleMedium?.copyWith(color: c.textPrimary),
            ),
          ),
          if (body != null) ...[
            const SizedBox(height: AppSpacing.xs),
            Text(
              body!,
              textAlign: TextAlign.center,
              style: context.text.bodySmall?.copyWith(color: c.textMuted),
            ),
          ],
          if (action != null) ...[
            const SizedBox(height: AppSpacing.lg),
            action!,
          ],
        ],
      ),
    );
  }
}

/// Soft tone ring around a round badge (kit sign-in / empty icon).
class _Halo extends StatelessWidget {
  const _Halo({required this.tone, required this.size, required this.child});

  final Tone tone;
  final double size;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final t = context.colors.tone(tone);
    return Container(
      width: size,
      height: size,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: t.bg.withValues(alpha: 0.5),
      ),
      child: child,
    );
  }
}

/// Round icon on a soft halo (sign-in header, big confirmations).
class HaloIcon extends StatelessWidget {
  const HaloIcon({required this.icon, super.key, this.size = 96});

  final AppIconData icon;
  final double size;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final inner = size * 0.7;
    return ExcludeSemantics(
      child: Container(
        width: size,
        height: size,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: c.brandAccent.withValues(alpha: 0.16),
        ),
        child: Container(
          width: inner,
          height: inner,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            gradient: LinearGradient(
              begin: Alignment.topCenter,
              end: Alignment.bottomCenter,
              colors: [c.brandAccentHover, c.brandAccent],
            ),
          ),
          child: AppIcon(icon, size: inner * 0.45, color: c.brandOnPrimary),
        ),
      ),
    );
  }
}

/// A grey placeholder block of the loading skeleton (pulses gently).
class SkeletonBox extends StatefulWidget {
  const SkeletonBox({super.key, this.width, this.height = 16, this.radius});

  final double? width;
  final double height;
  final double? radius;

  @override
  State<SkeletonBox> createState() => _SkeletonBoxState();
}

class _SkeletonBoxState extends State<SkeletonBox>
    with SingleTickerProviderStateMixin {
  late final _pulse = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 900),
    lowerBound: 0.5,
  )..repeat(reverse: true);

  @override
  void dispose() {
    _pulse.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    return FadeTransition(
      opacity: _pulse,
      child: Container(
        width: widget.width,
        height: widget.height,
        decoration: BoxDecoration(
          color: c.borderDefault,
          borderRadius: BorderRadius.circular(widget.radius ?? AppRadius.xs),
        ),
      ),
    );
  }
}

/// Loading skeleton of a list page: [count] card-shaped placeholders.
class SkeletonList extends StatelessWidget {
  const SkeletonList({super.key, this.count = 4, this.label});

  final int count;

  /// Announced to screen readers ("Yükleniyor…").
  final String? label;

  @override
  Widget build(BuildContext context) {
    final text = label ?? AppLocalizations.of(context).loading;
    return Semantics(
      liveRegion: true,
      label: text,
      child: ExcludeSemantics(
        child: Column(
          children: [
            for (var i = 0; i < count; i++) ...[
              const AppCard(
                child: Row(
                  children: [
                    SkeletonBox(width: 40, height: 40, radius: AppRadius.sm),
                    SizedBox(width: AppSpacing.sm),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          SkeletonBox(width: 160),
                          SizedBox(height: AppSpacing.xs),
                          SkeletonBox(width: 220, height: 12),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: AppSpacing.xs),
            ],
          ],
        ),
      ),
    );
  }
}

/// One step of a scan flow ("Raf okut", "Ürün okut", "Adet").
@immutable
class ScanStep {
  const ScanStep(this.label, {this.detail, this.skipped = false});

  final String label;

  /// The value once known (shelf code, SKU) or what is expected.
  final String? detail;

  /// Not needed for this line (no shelf tracking).
  final bool skipped;
}

/// The big "1 Raf okut → 2 Ürün okut → 3 Adet" indicator of the scan
/// screens: the current step is filled green, finished steps show a check,
/// the rest are outlined. Screen readers hear "Adım 2 / 3: Ürün okut".
class ScanStepIndicator extends StatelessWidget {
  const ScanStepIndicator({
    required this.steps,
    required this.current,
    super.key,
  });

  final List<ScanStep> steps;

  /// Index of the current step; `steps.length` = all done.
  final int current;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = context.colors;
    final active = current.clamp(0, steps.length - 1);
    final semantics = current >= steps.length
        ? l10n.scanStepAllDone
        : l10n.scanStepSemantics(active + 1, steps.length, steps[active].label);
    final children = <Widget>[];
    for (var i = 0; i < steps.length; i++) {
      if (i > 0) {
        children.add(
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: 17),
              child: Container(
                height: 2,
                margin: const EdgeInsets.symmetric(horizontal: AppSpacing.xxs),
                decoration: BoxDecoration(
                  color: i <= current ? c.brandAccent : c.borderDefault,
                  borderRadius: BorderRadius.circular(AppRadius.full),
                ),
              ),
            ),
          ),
        );
      }
      children.add(_StepDot(index: i, step: steps[i], current: current));
    }
    return Semantics(
      container: true,
      liveRegion: true,
      label: semantics,
      child: ExcludeSemantics(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: children,
        ),
      ),
    );
  }
}

class _StepDot extends StatelessWidget {
  const _StepDot({
    required this.index,
    required this.step,
    required this.current,
  });

  final int index;
  final ScanStep step;
  final int current;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final done = index < current || step.skipped;
    final isCurrent = index == current && !step.skipped;
    final Color fill;
    final Color fg;
    final Color border;
    if (isCurrent) {
      fill = c.brandPrimary;
      fg = c.brandOnPrimary;
      border = c.brandPrimary;
    } else if (done) {
      fill = c.success.bg;
      fg = c.success.fg;
      border = c.success.border;
    } else {
      fill = c.surfaceCard;
      fg = c.textMuted;
      border = c.borderDefault;
    }
    return SizedBox(
      width: 84,
      child: Column(
        children: [
          AnimatedContainer(
            duration: AppMotion.base,
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: fill,
              shape: BoxShape.circle,
              border: Border.all(color: border, width: 1.5),
              boxShadow: isCurrent
                  ? [BoxShadow(color: c.brand.bg, spreadRadius: 4)]
                  : null,
            ),
            alignment: Alignment.center,
            child: done && !isCurrent
                ? AppIcon(
                    step.skipped ? AppIcons.minus4 : AppIcons.check,
                    color: fg,
                    size: AppSize.icon,
                  )
                : Text(
                    '${index + 1}',
                    style: context.text.titleMedium?.bold.copyWith(color: fg),
                  ),
          ),
          const SizedBox(height: AppSpacing.xxs),
          Text(
            step.label,
            textAlign: TextAlign.center,
            maxLines: 2,
            style:
                (isCurrent
                        ? context.text.bodySmall?.semiBold
                        : context.text.bodySmall?.medium)
                    ?.copyWith(
                      color: isCurrent ? c.brandPrimary : c.textSecondary,
                    ),
          ),
          if (step.detail case final detail? when !step.skipped)
            Text(
              detail,
              textAlign: TextAlign.center,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: context.text.labelMedium?.tabular.copyWith(
                color: c.textMuted,
              ),
            ),
        ],
      ),
    );
  }
}

/// Tone of a scan result box.
enum FeedbackTone { success, info, warning, error }

/// The result of a scan / write, impossible to miss: success and error are
/// solid colour blocks with a big icon (readable at arm's length, in bright
/// light and with sunglasses); info and warning stay tinted. Each new
/// message pops in once (scale) so a repeated result is still noticed.
class FeedbackBanner extends StatelessWidget {
  const FeedbackBanner({
    required this.tone,
    required this.title,
    super.key,
    this.text,
    this.trailing,
  });

  final FeedbackTone tone;
  final String title;
  final String? text;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final c = context.colors;
    final (AppTone palette, AppIconData icon) = switch (tone) {
      FeedbackTone.success => (c.success, AppIcons.checkCircle1),
      FeedbackTone.info => (c.info, AppIcons.infoCircle),
      FeedbackTone.warning => (c.warning, AppIcons.warning),
      FeedbackTone.error => (c.danger, AppIcons.closeRemove),
    };
    final solid = tone == FeedbackTone.success || tone == FeedbackTone.error;
    final bg = solid
        ? (tone == FeedbackTone.error ? c.dangerSolid : c.successSolid)
        : palette.bg;
    final fg = solid ? c.onSolid : palette.fg;
    return TweenAnimationBuilder<double>(
      // A new message (tone + title) restarts the pop.
      key: ValueKey('$tone|$title|$text'),
      tween: Tween(begin: solid ? 0.94 : 1, end: 1),
      duration: AppMotion.slow,
      curve: Curves.easeOutBack,
      builder: (context, scale, child) =>
          Transform.scale(scale: scale, child: child),
      child: Semantics(
        liveRegion: true,
        container: true,
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: bg,
            border: Border.all(color: solid ? bg : palette.border),
            borderRadius: AppRadius.mdAll,
          ),
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.sm),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                AppIcon(icon, color: fg, size: solid ? 32 : AppSize.iconMd),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        title,
                        style:
                            (solid
                                    ? context.text.titleLarge
                                    : context.text.titleMedium)
                                ?.copyWith(color: fg),
                      ),
                      if (text case final text?)
                        Text(
                          text,
                          style: context.text.bodyMedium?.copyWith(color: fg),
                        ),
                    ],
                  ),
                ),
                ?trailing,
              ],
            ),
          ),
        ),
      ),
    );
  }
}
