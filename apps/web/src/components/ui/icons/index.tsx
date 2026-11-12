/**
 * Central icon module (#222) — every icon in the app is imported from here, never from an icon library directly.
 *
 * - `<AppIcon name="search" />` renders a UI-kit icon (packages/design-tokens/icons, generated into ./data.ts).
 *   Monochrome icons draw with `currentColor`, so they take the text colour (`text-fg-muted`, `text-brand`, …).
 * - The named exports keep the component-per-icon API the screens already use (`<Plus className="size-4" />`).
 *   Names with a kit counterpart render the kit icon; the rest fall back to lucide-react so the set stays complete.
 *   Swapping a fallback for a kit icon later is a one-line change in KIT below.
 *
 * Icons are decorative by default (aria-hidden); pass `aria-label` or `title` to make one meaningful.
 */
import { createElement, type ComponentType, type ReactNode, type SVGProps } from "react";
import { ICONS, type IconName, type IconNode } from "./data";

export { ICONS, type IconName } from "./data";

export type IconProps = Omit<SVGProps<SVGSVGElement>, "ref" | "name"> & {
  size?: number | string;
  /** Accepted for lucide compatibility; kit icons keep their designed stroke. */
  strokeWidth?: number | string;
  absoluteStrokeWidth?: boolean;
};

export type IconComponent = ComponentType<IconProps>;

function renderNodes(nodes: readonly IconNode[] | undefined, prefix: string): ReactNode[] | undefined {
  return nodes?.map(([tag, attrs, children], i) =>
    createElement(tag, { key: `${prefix}${i}`, ...attrs }, renderNodes(children, `${prefix}${i}.`)),
  );
}

export function AppIcon({
  name,
  size = 24,
  className,
  strokeWidth: _strokeWidth, // eslint-disable-line @typescript-eslint/no-unused-vars -- dropped: kit stroke is fixed
  absoluteStrokeWidth: _absolute, // eslint-disable-line @typescript-eslint/no-unused-vars
  title,
  ...rest
}: IconProps & { name: IconName; title?: string }) {
  const icon = ICONS[name];
  const labelled = Boolean(rest["aria-label"] || title);
  return createElement(
    "svg",
    {
      xmlns: "http://www.w3.org/2000/svg",
      width: size,
      height: size,
      viewBox: icon.viewBox,
      fill: "none",
      focusable: "false",
      "aria-hidden": labelled ? undefined : true,
      role: labelled ? "img" : undefined,
      "data-icon": name,
      className: ["shrink-0", className].filter(Boolean).join(" "),
      ...rest,
    },
    title ? createElement("title", { key: "title" }, title) : null,
    renderNodes(icon.nodes as readonly IconNode[], ""),
  );
}

function kit(name: IconName, displayName: string): IconComponent {
  const Icon = (props: IconProps) => <AppIcon name={name} {...props} />;
  Icon.displayName = displayName;
  return Icon;
}

/* ── Kit-backed icons, under the names the screens use ─────────────────────────────────────────────────────────── */
// Navigation
export const LayoutDashboard = kit("dashboard_1", "LayoutDashboard");
export const ChartColumn = kit("vertical_bar_chart_1", "ChartColumn");
export const ShoppingCart = kit("buy", "ShoppingCart");
export const Users = kit("3_user", "Users");
export const UsersRound = kit("2_user", "UsersRound");
export const Tags = kit("tag_01", "Tags");
export const Tag = kit("tag_01", "Tag");
export const Package = kit("package", "Package");
export const Boxes = kit("package_open", "Boxes");
export const Warehouse = kit("store_03", "Warehouse");
export const ClipboardList = kit("document", "ClipboardList");
export const Truck = kit("delivery_2", "Truck");
export const Settings = kit("setting", "Settings");
export const FileUp = kit("cloud_upload", "FileUp");
export const Upload = kit("cloud_upload", "Upload");
export const Receipt = kit("invoice_03", "Receipt");
// Actions
export const Plus = kit("plus_4", "Plus");
export const Minus = kit("minus_4", "Minus");
export const Search = kit("search", "Search");
export const X = kit("close_remove", "X");
export const Pencil = kit("edit", "Pencil");
export const Trash2 = kit("delete_2", "Trash2");
export const SlidersHorizontal = kit("filter_2", "SlidersHorizontal");
export const ListFilter = kit("filter_2", "ListFilter");
export const ScanBarcode = kit("scan", "ScanBarcode");
export const ScanText = kit("scan", "ScanText");
export const Menu = kit("menu_1", "Menu");
export const PanelLeftClose = kit("collaps", "PanelLeftClose");
// Arrows
export const ArrowLeft = kit("arrow_left_2", "ArrowLeft");
export const ArrowRight = kit("arrow_right_2", "ArrowRight");
export const ChevronLeft = kit("arrow_left_2", "ChevronLeft");
export const ChevronRight = kit("arrow_right_2", "ChevronRight");
export const ChevronDown = kit("arrow_down_2", "ChevronDown");
export const ChevronUp = kit("arrow_up_2", "ChevronUp");
export const TrendingUp = kit("trend_up_graph", "TrendingUp");
export const TrendingDown = kit("trend_down_graph", "TrendingDown");
// Status, people, misc
export const CircleCheck = kit("check_circle_1", "CircleCheck");
export const Info = kit("info_circle", "Info");
export const CircleHelp = kit("question_mark_circle", "CircleHelp");
export const Lock = kit("lock", "Lock");
export const EyeOff = kit("hide", "EyeOff");
export const CalendarRange = kit("calendar", "CalendarRange");
export const Calendar = kit("calendar", "Calendar");
export const Bell = kit("ringtone_3", "Bell");
export const Mail = kit("email_1", "Mail");
export const MailCheck = kit("email_open_3", "MailCheck");
export const UserPlus = kit("add_user", "UserPlus");
export const UserRound = kit("profile", "UserRound");
export const Building2 = kit("store_14", "Building2");
export const MapPin = kit("pin_3", "MapPin");
export const PackageCheck = kit("shopping_bag_check", "PackageCheck");
export const PackagePlus = kit("shopping_cart_add_01", "PackagePlus");
export const FileSpreadsheet = kit("dollar_document", "FileSpreadsheet");
export const Sun = kit("sun_03", "Sun");
export const WhatsApp = kit("whatsapp_logomark", "WhatsApp");

/* ── No kit counterpart: lucide fallback (same stroke family, recoloured by currentColor) ───────────────────────── */
export {
  Archive,
  ArchiveRestore,
  ArrowLeftRight,
  ArrowRightLeft,
  Ban,
  Bot,
  Calculator,
  Check,
  CheckCheck,
  Circle,
  CircleAlert,
  CircleDashed,
  CirclePause,
  CircleX,
  ClipboardCheck,
  Clock,
  Construction,
  Copy,
  Crown,
  Download,
  FileCheck2,
  FileDown,
  Flag,
  FolderPlus,
  FolderTree,
  Gauge,
  Gift,
  History,
  Hourglass,
  Inbox,
  Layers,
  Lightbulb,
  ListChecks,
  ListPlus,
  LoaderCircle,
  LogIn,
  LogOut,
  MailPlus,
  MessageSquareText,
  PackageMinus,
  PanelLeftOpen,
  Play,
  Puzzle,
  RefreshCw,
  Repeat,
  Rocket,
  RotateCcw,
  Rows3,
  Rows4,
  Ruler,
  Save,
  SearchX,
  Send,
  ShieldAlert,
  ShieldCheck,
  Shuffle,
  Sparkles,
  Star,
  TriangleAlert,
  Trophy,
  Undo2,
  UserCog,
  XCircle,
} from "lucide-react";
