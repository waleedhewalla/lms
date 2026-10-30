/** Types and pure helpers for the correspondence screen (kept apart from the page so they can be unit-tested). */

export type Direction = "Incoming" | "Outgoing" | "Internal";
export const DIRECTIONS: Direction[] = ["Incoming", "Outgoing", "Internal"];
export const STATUSES = ["Draft", "Submitted", "InReview", "Approved", "Rejected", "Archived"] as const;
export const PRIORITIES = ["Normal", "High", "Urgent"] as const;
export const SLIP_ACTIONS = ["DraftOfficialReply", "UrgentExecution", "StudyAndAdvise", "ForInformation"] as const;

export type RecipientRow = { displayName: string; isExternal: boolean; kind: "To" | "Cc" };
export type RegisterRow = {
  id: string; number: string; type: Direction; subject: string; status: string; priority: string; isConfidential: boolean;
  createdAt: string; authorId: string; parentCorrespondenceId: string | null; recipients: RecipientRow[];
};
export type RegisterPage = { total: number; skip: number; take: number; items: RegisterRow[] };

export type RegisterFilter = { tenantId: string; year?: number; type?: string; status?: string; q?: string; skip?: number; take?: number };

/** Query string for /api/correspondence/register and /register/export (empty filters are left out). */
export function registerQuery(f: RegisterFilter, extra: Record<string, string> = {}): string {
  const p = new URLSearchParams({ tenantId: f.tenantId });
  if (f.year && Number.isInteger(f.year)) p.set("year", String(f.year));
  if (f.type) p.set("type", f.type);
  if (f.status) p.set("status", f.status);
  if (f.q?.trim()) p.set("q", f.q.trim());
  if (f.skip && f.skip > 0) p.set("skip", String(f.skip));
  if (f.take) p.set("take", String(f.take));
  for (const [k, v] of Object.entries(extra)) p.set(k, v);
  return p.toString();
}

export const recipientsOf = (row: Pick<RegisterRow, "recipients">, kind: "To" | "Cc") =>
  row.recipients.filter((r) => r.kind === kind).map((r) => r.displayName);

/** "Re: subject", without stacking prefixes on a subject that already has one. */
export const replySubject = (subject: string) => (/^re:\s/i.test(subject) ? subject : `Re: ${subject}`);

/** The direction a reply travels: an answer to an incoming letter goes out; anything else stays where it is. */
export const replyDirection = (d: Direction): Direction => (d === "Incoming" ? "Outgoing" : d);

/** Page window for the "x–y of z" label and the previous/next buttons. */
export function pageWindow(page: Pick<RegisterPage, "total" | "skip" | "take">) {
  const from = page.total === 0 ? 0 : page.skip + 1;
  const to = Math.min(page.skip + page.take, page.total);
  return { from, to, hasPrev: page.skip > 0, hasNext: page.skip + page.take < page.total };
}
