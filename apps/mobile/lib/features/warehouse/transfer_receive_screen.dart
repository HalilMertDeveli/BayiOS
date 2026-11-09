import 'dart:async';

import 'package:bayios_mobile/core/ui/tokens.dart';
import 'package:bayios_mobile/core/ui/widgets.dart';
import 'package:bayios_mobile/features/picking/scan_alerts.dart';
import 'package:bayios_mobile/features/picking/tasks_widgets.dart';
import 'package:bayios_mobile/features/stock/quantity.dart';
import 'package:bayios_mobile/features/stock/stock_api.dart';
import 'package:bayios_mobile/features/stock/stock_errors.dart';
import 'package:bayios_mobile/features/stock/stock_widgets.dart';
import 'package:bayios_mobile/features/warehouse/document_scan_controller.dart';
import 'package:bayios_mobile/features/warehouse/transfer_list_screen.dart';
import 'package:bayios_mobile/features/warehouse/transfer_receive_controller.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_api.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_errors.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_models.dart';
import 'package:bayios_mobile/features/warehouse/warehouse_widgets.dart';
import 'package:bayios_mobile/l10n/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Transfer teslim alma: shelf → quantity → product scan, one receipt per
/// scan (partial receipts allowed). Dispatching / closing is on the web.
class TransferReceiveScreen extends ConsumerStatefulWidget {
  const TransferReceiveScreen({required this.transferId, super.key});

  final String transferId;

  @override
  ConsumerState<TransferReceiveScreen> createState() =>
      _TransferReceiveScreenState();
}

class _TransferReceiveScreenState extends ConsumerState<TransferReceiveScreen> {
  late final TransferReceiveController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TransferReceiveController(
      api: ref.read(warehouseApiProvider),
      stock: ref.read(stockApiProvider),
      transferId: widget.transferId,
      alerts: ref.read(scanAlertsProvider),
      wait: ref.read(stockDelayProvider),
    );
    // Without the permission the screen only explains (no API call).
    if (ref
        .read(stockAccessProvider)
        .canSome(WarehousePermissions.receiveTransfer)) {
      unawaited(_controller.load());
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final access = ref.watch(stockAccessProvider);
    if (!access.canSome(WarehousePermissions.receiveTransfer)) {
      return WarehouseForbidden(
        title: l10n.transferReceiveTitle,
        message: l10n.transferForbidden,
      );
    }
    return ListenableBuilder(
      listenable: _controller,
      builder: (context, _) {
        final c = _controller;
        final Widget body;
        if (c.loading && c.document == null) {
          body = const LoadingView();
        } else if (c.document == null) {
          body = Center(
            child: StockMessagePanel(
              key: const Key('transfer-load-error'),
              icon: Icons.cloud_off_outlined,
              tone: BosColors.danger,
              message: documentErrorMessage(
                l10n,
                c.loadError!,
                forbidden: l10n.transferForbidden,
              ),
              onRetry: c.load,
              error: c.loadError,
            ),
          );
        } else {
          body = RefreshIndicator(
            onRefresh: c.load,
            child: _ReceiveBody(controller: c),
          );
        }
        return Scaffold(
          appBar: AppBar(
            title: Text(c.transfer?.number ?? l10n.transferReceiveTitle),
          ),
          body: body,
        );
      },
    );
  }
}

class _ReceiveBody extends StatelessWidget {
  const _ReceiveBody({required this.controller});

  final TransferReceiveController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final detail = c.document!;
    final transfer = detail.transfer;
    final (label, tone) = transferStatusMeta(l10n, transfer.status);
    final showShelf = c.warehouse == null || c.requiresShelf;
    return ListView(
      key: const Key('transfer-receive'),
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(BosSpace.s16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(BosSpace.s16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        l10n.transferRoute(
                          transfer.sourceWarehouseName ?? '—',
                          transfer.destinationWarehouseName ?? '—',
                        ),
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                    ),
                    ToneBadge(label, tone),
                  ],
                ),
                const SizedBox(height: BosSpace.s4),
                Semantics(
                  liveRegion: true,
                  child: Text(
                    l10n.transferReceivedSummary(
                      formatQuantity(transfer.receivedQuantity),
                      formatQuantity(transfer.quantity),
                      formatQuantity(transfer.inTransitQuantity),
                    ),
                    key: const Key('transfer-summary'),
                  ),
                ),
                if (transfer.note case final String note when note.isNotEmpty)
                  Text(
                    note,
                    style: const TextStyle(color: BosColors.textMuted),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: BosSpace.s16),
        if (!c.writable)
          StockNotice(
            key: const Key('transfer-not-receivable'),
            tone: transfer.status == TransferStatus.received
                ? BosColors.success
                : BosColors.warning,
            icon: transfer.status == TransferStatus.received
                ? Icons.check_circle_outline
                : Icons.info_outline,
            child: Text(
              transfer.status == TransferStatus.received
                  ? l10n.transferAllReceived
                  : l10n.transferNotReceivable,
            ),
          )
        else ...[
          DocumentScanSteps(
            key: const Key('transfer-steps'),
            shelf: c.shelf,
            shelfRequired: showShelf,
            quantityText: c.quantityText,
          ),
          const SizedBox(height: BosSpace.s16),
          if (showShelf) ...[
            ShelfScanField(
              keyPrefix: 'transfer',
              shelf: c.shelf,
              enabled: !c.sending && !c.hasPending,
              required: c.requiresShelf,
              missing: c.entryProblem == EntryProblem.shelfRequired,
              hint: l10n.transferShelfHint,
              onScanned: c.setShelf,
              onClear: c.clearShelf,
            ),
            const SizedBox(height: BosSpace.s16),
          ],
          QuantityStepper(
            keyPrefix: 'transfer',
            text: c.quantityText,
            label: l10n.docQuantityLabel,
            error: quantityErrorText(l10n, c.quantityError),
            enabled: !c.sending && !c.hasPending,
            onChanged: c.setQuantityText,
            onStep: c.stepQuantity,
          ),
          const SizedBox(height: BosSpace.s16),
          CodeEntryField(
            fieldKey: const Key('transfer-code-field'),
            scanKey: const Key('transfer-code-scan'),
            label: l10n.docProductLabel,
            hint: l10n.transferProductHint,
            scanTitle: l10n.docProductScanTitle,
            enabled: c.canScan,
            busy: c.sending,
            onSubmit: c.enter,
          ),
        ],
        if (c.retryingBusy) ...[
          const SizedBox(height: BosSpace.s8),
          Text(l10n.docBusyRetrying, key: const Key('transfer-busy-retry')),
        ],
        if (c.feedback case final EntryFeedback feedback) ...[
          const SizedBox(height: BosSpace.s12),
          EntryFeedbackView(
            keyPrefix: 'transfer',
            feedback: feedback,
            sending: c.sending,
            forbidden: l10n.transferForbidden,
            acceptedLine: (line) => l10n.transferAcceptedLine(
              line.sku,
              signedQuantity(line.quantity),
              formatQuantity(line.total),
            ),
            onResend: c.resend,
            onDiscard: c.discardPending,
          ),
        ],
        const SizedBox(height: BosSpace.s16),
        Text(
          l10n.transferLinesTitle(detail.lines.length),
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: BosSpace.s8),
        for (final line in detail.sortedLines) _LineTile(line: line),
      ],
    );
  }
}

class _LineTile extends StatelessWidget {
  const _LineTile({required this.line});

  final TransferLine line;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    return Card(
      key: Key('transfer-line-${line.id}'),
      child: Padding(
        padding: const EdgeInsets.all(BosSpace.s12),
        child: Row(
          children: [
            Icon(
              line.done ? Icons.check_circle : Icons.local_shipping_outlined,
              color: line.done ? BosColors.success.fg : BosColors.textMuted,
            ),
            const SizedBox(width: BosSpace.s12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(line.sku, style: BosType.mono.copyWith(fontSize: 17)),
                  Text(
                    l10n.transferLineProgress(
                      formatQuantity(line.receivedQuantity),
                      formatQuantity(line.quantity),
                    ),
                    style: theme.textTheme.bodyLarge,
                  ),
                  if (!line.done)
                    Text(
                      line.destinationLocationCode == null
                          ? l10n.transferLineOpen(
                              formatQuantity(line.inTransitQuantity),
                            )
                          : l10n.transferLineOpenShelf(
                              formatQuantity(line.inTransitQuantity),
                              line.destinationLocationCode!,
                            ),
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: BosColors.textMuted,
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
