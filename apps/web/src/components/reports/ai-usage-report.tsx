"use client";

import { useId } from "react";
import { Bot } from "@/components/ui/icons";
import { EmptyState } from "@/components/ui/empty-state";
import { Table, type TableColumn } from "@/components/ui/table";
import { getAiUsage, type AiUsageReport } from "@/lib/reports/api";
import { formatDay, listDays } from "@/lib/reports/date-range";
import { reportDefinition } from "@/lib/reports/definitions";
import { useSession } from "@/lib/session/session-context";
import { DailyChart, fillDays } from "./daily-chart";
import { DateRangeFilter, InvalidRange, ReportError, ReportFrame, ReportGate, ReportSection, StatList, count } from "./report-frame";
import { useReport, useReportUrl } from "./use-report";

type DayRow = AiUsageReport["days"][number];

const REPORT = reportDefinition("ai-usage");

/** AI assistant counters (integrations.ai_usage) by day — owner/admin (billing.read). No message text, no customer data. */
export function AiUsageReportView() {
  return (
    <ReportGate report={REPORT}>
      <AiUsageReportBody />
    </ReportGate>
  );
}

function AiUsageReportBody() {
  const uid = useId();
  const { api } = useSession();
  const { today, range, rangeError, setRange } = useReportUrl();
  const key = rangeError ? null : JSON.stringify(range);
  const { state, reload } = useReport(key, (signal) => getAiUsage(api, range, signal));
  const report = state.status === "ready" ? state.data : null;
  const totals = report?.totals;
  const empty = totals && Number(totals.parseRequests) === 0 && Number(totals.aiCalls) === 0;

  const columns: TableColumn<DayRow>[] = [
    { key: "day", header: "Gün", width: "9rem", cell: (r) => formatDay(r.day) },
    { key: "parses", header: "Ayrıştırma", numeric: true, cell: (r) => count(r.parseRequests) },
    { key: "calls", header: "AI çağrısı", numeric: true, cell: (r) => count(r.aiCalls) },
    { key: "failures", header: "Hata", numeric: true, cell: (r) => count(r.aiFailures) },
    { key: "fallbacks", header: "Kural tabanlı", numeric: true, cell: (r) => count(r.fallbacks) },
    { key: "in", header: "Girdi token", numeric: true, cell: (r) => count(r.inputTokens) },
    { key: "out", header: "Çıktı token", numeric: true, cell: (r) => count(r.outputTokens) },
  ];

  return (
    <ReportFrame
      report={REPORT}
      filters={<DateRangeFilter range={range} today={today} onChange={setRange} />}
      exportFilters={rangeError ? null : { from: range.from, to: range.to }}
      today={today}
    >
      {rangeError ? (
        <InvalidRange message={rangeError} />
      ) : state.status === "error" ? (
        <ReportError message={state.message} onRetry={reload} />
      ) : empty ? (
        <div className="rounded-xl border border-line bg-card shadow-e1">
          <EmptyState icon={<Bot className="size-6" />} title="Bu aralıkta AI kullanımı yok" description="Seçilen günlerde WhatsApp mesajı ayrıştırılmadı." />
        </div>
      ) : (
        <>
          <StatList
            label="AI kullanım özeti"
            loading={!totals}
            stats={[
              { label: "Ayrıştırma", value: count(totals?.parseRequests) },
              { label: "AI çağrısı", value: count(totals?.aiCalls) },
              { label: "Hata", value: count(totals?.aiFailures) },
              { label: "Kural tabanlı geri dönüş", value: count(totals?.fallbacks) },
              {
                label: "Token (girdi / çıktı)",
                value: totals ? `${count(totals.inputTokens)} / ${count(totals.outputTokens)}` : "—",
              },
            ]}
          />
          {report ? (
            <DailyChart
              title="Günlük ayrıştırma"
              label="Günlük ayrıştırma"
              points={fillDays(listDays(report), report.days.map((d) => ({ day: d.day, value: Number(d.parseRequests) })))}
            />
          ) : null}
          <ReportSection id={`${uid}-ai-by-day`} title="Günlük döküm">
            <Table caption="Günlük AI kullanımı" columns={columns} rows={report?.days ?? []} getRowId={(r) => r.day} loading={!report} loadingRows={3} maxHeight="28rem" density="compact" />
          </ReportSection>
        </>
      )}
    </ReportFrame>
  );
}
