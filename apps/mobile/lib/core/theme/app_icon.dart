import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

/// One icon of the app: a kit SVG (`assets/icons/<name>.svg`, 24×24,
/// `currentColor` strokes) or — only where the kit has no reasonable
/// equivalent — a Material icon. Screens never use `Icons.*` directly; they
/// pick from [AppIcons] so the icon set stays in one place.
@immutable
class AppIconData {
  /// Monochrome kit icon, tinted with the icon colour.
  const AppIconData(this.name) : material = null, multicolor = false;

  /// Multicolour kit icon (never tinted).
  const AppIconData.multicolor(this.name) : material = null, multicolor = true;

  /// Material fallback (no kit equivalent).
  const AppIconData.material(IconData this.material)
    : name = null,
      multicolor = false;

  final String? name;
  final IconData? material;
  final bool multicolor;

  String? get assetPath => name == null ? null : 'assets/icons/$name.svg';

  @override
  bool operator ==(Object other) =>
      other is AppIconData &&
      other.name == name &&
      other.material == material &&
      other.multicolor == multicolor;

  @override
  int get hashCode => Object.hash(name, material, multicolor);

  @override
  String toString() => 'AppIconData(${name ?? material})';
}

/// Draws an [AppIconData]. Size and colour default to the ambient
/// [IconTheme] (buttons, app bar, navigation bar set it), like [Icon].
/// Decorative unless [semanticLabel] is given.
class AppIcon extends StatelessWidget {
  const AppIcon(
    this.icon, {
    super.key,
    this.size,
    this.color,
    this.semanticLabel,
  });

  final AppIconData icon;
  final double? size;
  final Color? color;
  final String? semanticLabel;

  @override
  Widget build(BuildContext context) {
    final theme = IconTheme.of(context);
    final size = this.size ?? theme.size ?? 24;
    final material = icon.material;
    if (material != null) {
      return Icon(
        material,
        size: size,
        color: this.color,
        semanticLabel: semanticLabel,
      );
    }
    final color = this.color ?? theme.color;
    final picture = SvgPicture.asset(
      icon.assetPath!,
      width: size,
      height: size,
      excludeFromSemantics: true,
      colorFilter: icon.multicolor || color == null
          ? null
          : ColorFilter.mode(color, BlendMode.srcIn),
    );
    // Like [Icon]: a size×size box with the glyph centred, so tight parent
    // constraints (input prefixes, nav bars) never stretch the drawing.
    final box = SizedBox(
      width: size,
      height: size,
      child: Center(child: picture),
    );
    final label = semanticLabel;
    if (label == null) return ExcludeSemantics(child: box);
    return Semantics(label: label, image: true, child: box);
  }
}

/// The icon set: the 93 kit SVGs (names = file names in `assets/icons/`)
/// and the few Material fallbacks at the end.
abstract final class AppIcons {
  static const twoUser = AppIconData('2_user');
  static const twoUser2 = AppIconData('2_user_2');
  static const threeUser = AppIconData('3_user');
  static const addUser = AppIconData('add_user');
  static const addUser2 = AppIconData('add_user_2');
  static const arrowDown2 = AppIconData('arrow_down_2');
  static const arrowLeft2 = AppIconData('arrow_left_2');
  static const arrowRight2 = AppIconData('arrow_right_2');
  static const arrowUp2 = AppIconData('arrow_up_2');
  static const bag = AppIconData('bag');
  static const buy = AppIconData('buy');
  static const calendar = AppIconData('calendar');
  static const chartThreeLine3 = AppIconData('chart_three_line_3');
  static const checkCircle1 = AppIconData('check_circle_1');
  static const closeRemove = AppIconData('close_remove');
  static const cloudUpload = AppIconData('cloud_upload');
  static const collaps = AppIconData('collaps');
  static const dashboard1 = AppIconData('dashboard_1');
  static const delete2 = AppIconData('delete_2');
  static const delivery2 = AppIconData('delivery_2');
  static const delivery3 = AppIconData('delivery_3');
  static const delivery4 = AppIconData('delivery_4');
  static const delivery5 = AppIconData('delivery_5');
  static const delivery6 = AppIconData('delivery_6');
  static const delivery7 = AppIconData('delivery_7');
  static const document = AppIconData('document');
  static const dollarBadge = AppIconData('dollar_badge');
  static const dollarBadge2 = AppIconData('dollar_badge_2');
  static const dollarBadge3 = AppIconData('dollar_badge_3');
  static const dollarCircle3 = AppIconData('dollar_circle_3');
  static const dollarDocument = AppIconData('dollar_document');
  static const dollarDocument2 = AppIconData('dollar_document_2');
  static const dollarDown = AppIconData('dollar_down');
  static const dollarRefresh = AppIconData('dollar_refresh');
  static const dollarSticker = AppIconData('dollar_sticker');
  static const dollarUp = AppIconData('dollar_up');
  static const edit = AppIconData('edit');
  static const email1 = AppIconData('email_1');
  static const emailNotification = AppIconData('email_notification');
  static const emailOpen3 = AppIconData('email_open_3');
  static const filter2 = AppIconData('filter_2');
  static const hide = AppIconData('hide');
  static const image2 = AppIconData('image_2');
  static const infoCircle = AppIconData('info_circle');
  static const infoMenu2 = AppIconData('info_menu_2');
  static const infoMenu3 = AppIconData('info_menu_3');
  static const invoice03 = AppIconData('invoice_03');
  static const invoice04 = AppIconData('invoice_04');
  static const lineCaretleft = AppIconData('line_caretleft');
  static const lock = AppIconData('lock');
  static const menu1 = AppIconData('menu_1');
  static const minus4 = AppIconData('minus_4');
  static const moneyPaperDrop = AppIconData('money_paper_drop');
  static const moneyPaperDrop2 = AppIconData('money_paper_drop_2');
  static const moneyPaperTotal = AppIconData('money_paper_total');
  static const moneyPaperTotal2 = AppIconData('money_paper_total_2');
  static const mousePointer = AppIconData('mouse_pointer');
  static const package = AppIconData('package');
  static const packageMoving = AppIconData('package_moving');
  static const packageOpen = AppIconData('package_open');
  static const paymentSuccess02 = AppIconData('payment_success_02');
  static const pencil5 = AppIconData('pencil_5');
  static const pin3 = AppIconData('pin_3');
  static const plus4 = AppIconData('plus_4');
  static const preferences2 = AppIconData('preferences_2');
  static const profile = AppIconData('profile');
  static const questionMarkCircle = AppIconData('question_mark_circle');
  static const receiptBill2 = AppIconData('receipt_bill_2');
  static const rightDownLine = AppIconData('right_down_line');
  static const rightUpBox = AppIconData('right_up_box');
  static const rightUpLine = AppIconData('right_up_line');
  static const ringtone3 = AppIconData('ringtone_3');
  static const scan = AppIconData('scan');
  static const search = AppIconData('search');
  static const setting = AppIconData('setting');
  static const setting9 = AppIconData('setting_9');
  static const shoppingBagCheck = AppIconData('shopping_bag_check');
  static const shoppingCartAdd01 = AppIconData('shopping_cart_add_01');
  static const store03 = AppIconData('store_03');
  static const store14 = AppIconData('store_14');
  static const store5 = AppIconData('store_5');
  static const store8 = AppIconData('store_8');
  static const store9 = AppIconData('store_9');
  static const sun03 = AppIconData.multicolor('sun_03');
  static const tag01 = AppIconData('tag_01');
  static const trendDownGraph = AppIconData('trend_down_graph');
  static const trendUpGraph = AppIconData('trend_up_graph');
  static const trolley01 = AppIconData('trolley_01');
  static const trolley02 = AppIconData('trolley_02');
  static const upEnter = AppIconData('up_enter');
  static const userStory = AppIconData('user_story');
  static const verticalBarChart1 = AppIconData('vertical_bar_chart_1');
  static const whatsappLogomark = AppIconData.multicolor('whatsapp_logomark');

  /// Every kit SVG (icon sheet in the screenshot test).
  static const List<AppIconData> kit = [
    twoUser,
    twoUser2,
    threeUser,
    addUser,
    addUser2,
    arrowDown2,
    arrowLeft2,
    arrowRight2,
    arrowUp2,
    bag,
    buy,
    calendar,
    chartThreeLine3,
    checkCircle1,
    closeRemove,
    cloudUpload,
    collaps,
    dashboard1,
    delete2,
    delivery2,
    delivery3,
    delivery4,
    delivery5,
    delivery6,
    delivery7,
    document,
    dollarBadge,
    dollarBadge2,
    dollarBadge3,
    dollarCircle3,
    dollarDocument,
    dollarDocument2,
    dollarDown,
    dollarRefresh,
    dollarSticker,
    dollarUp,
    edit,
    email1,
    emailNotification,
    emailOpen3,
    filter2,
    hide,
    image2,
    infoCircle,
    infoMenu2,
    infoMenu3,
    invoice03,
    invoice04,
    lineCaretleft,
    lock,
    menu1,
    minus4,
    moneyPaperDrop,
    moneyPaperDrop2,
    moneyPaperTotal,
    moneyPaperTotal2,
    mousePointer,
    package,
    packageMoving,
    packageOpen,
    paymentSuccess02,
    pencil5,
    pin3,
    plus4,
    preferences2,
    profile,
    questionMarkCircle,
    receiptBill2,
    rightDownLine,
    rightUpBox,
    rightUpLine,
    ringtone3,
    scan,
    search,
    setting,
    setting9,
    shoppingBagCheck,
    shoppingCartAdd01,
    store03,
    store14,
    store5,
    store8,
    store9,
    sun03,
    tag01,
    trendDownGraph,
    trendUpGraph,
    trolley01,
    trolley02,
    upEnter,
    userStory,
    verticalBarChart1,
    whatsappLogomark,
  ];

  // Material fallbacks: no reasonable kit equivalent.
  static const check = AppIconData.material(Icons.check_rounded);
  static const refresh = AppIconData.material(Icons.refresh_rounded);
  static const logout = AppIconData.material(Icons.logout_rounded);
  static const offline = AppIconData.material(Icons.cloud_off_outlined);
  static const wifiOff = AppIconData.material(Icons.wifi_off_rounded);
  static const warning = AppIconData.material(Icons.warning_amber_rounded);
  static const block = AppIconData.material(Icons.block_rounded);
  static const shelves = AppIconData.material(Icons.shelves);
  static const visibility = AppIconData.material(Icons.visibility_outlined);
  static const language = AppIconData.material(Icons.language_rounded);
  static const bolt = AppIconData.material(Icons.bolt_rounded);
  static const timelapse = AppIconData.material(Icons.timelapse_rounded);
  static const keyboard = AppIconData.material(Icons.keyboard_outlined);
  static const flashlight = AppIconData.material(Icons.flashlight_on_outlined);
  static const cameraOff = AppIconData.material(Icons.no_photography_outlined);
  static const videocamOff = AppIconData.material(Icons.videocam_off_outlined);
  static const handPointer = AppIconData.material(Icons.pan_tool_alt_outlined);
  static const syncProblem = AppIconData.material(Icons.sync_problem_rounded);
  static const swapHorizontal = AppIconData.material(Icons.swap_horiz_rounded);
  static const save = AppIconData.material(Icons.save_outlined);
  static const doneAll = AppIconData.material(Icons.done_all_rounded);
  static const playlistAdd = AppIconData.material(Icons.playlist_add_rounded);
}
