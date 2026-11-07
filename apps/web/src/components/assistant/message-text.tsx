import { messageSegments, type SegmentLine } from "@/lib/assistant/review";

/**
 * The pasted WhatsApp message, shown as UNTRUSTED PLAIN TEXT: every segment is a React text node (escaped by React), so
 * HTML, script tags, markdown and URLs stay inert characters — nothing is parsed, rendered or turned into a link. The
 * parts the order lines were read from are marked with their line number.
 */
export function MessageText({ message, lines }: { message: string; lines: readonly SegmentLine[] }) {
  const segments = messageSegments(message, lines);
  return (
    <p data-testid="whatsapp-message" className="text-body leading-7 break-words whitespace-pre-wrap text-fg">
      {segments.map((segment, i) =>
        segment.lineNo === null ? (
          segment.text
        ) : (
          <mark key={i} className="rounded-sm bg-ai-bg px-0.5 text-fg ring-1 ring-ai-border">
            <span className="mr-1 rounded-sm bg-ai-fg px-1 align-middle font-mono text-[0.7rem] leading-4 text-ai-bg select-none">
              <span className="sr-only">Satır </span>
              {segment.lineNo}
            </span>
            {segment.text}
          </mark>
        ),
      )}
    </p>
  );
}
