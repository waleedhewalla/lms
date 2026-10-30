/** Types and pure helpers for the correspondence screen (kept apart from the page so they can be unit-tested). */

export type Direction = "Incoming" | "Outgoing" | "Internal";
export const DIRECTIONS: Direction[] = ["Incoming", "Outgoing", "Internal"];
export const STATUSES = ["Draft", "Submitted", "InReview", "Approved", "Rejected", "Returned", "Closed", "Archived"] as const;
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

// ---- Types and workflows (step 3) ----

export type TypeDef = {
  id: string; code: string; nameEn: string; nameAr: string; purpose: string; senderDescription: string; receiverDescription: string;
  createPermission: string; requiresTargetUnit: boolean; requiresAmount: boolean; isActive: boolean;
};
export type Unit = { id: string; code: string; name: string; parentId: string | null };
export type HistoryStep = {
  id: string; assigneeId: string; assigneeName: string | null; status: string; dueAt: string; decidedAt: string | null;
  decidedBy: string | null; decidedByName: string | null; comment: string | null;
};
export type HistoryEvent = { at: string; action: string; actorName: string | null; details: string | null };
export type History = {
  correspondence: { id: string; number: string; subject: string; status: string; typeCode: string | null; amount: number | null };
  events: HistoryEvent[]; steps: HistoryStep[]; runs: { id: string; status: string; currentNodeId: string; updatedAt: string }[];
};

/** What the signed-in person may do with a letter. The server enforces the same rules; this only decides which buttons to show. */
export function letterActions(c: { status: string; typeCode?: string | null }, isAuthor: boolean, canCloseAny: boolean) {
  const typed = !!c.typeCode;
  const finished = ["Closed", "Rejected", "Archived"].includes(c.status);
  return {
    edit: typed && isAuthor && (c.status === "Draft" || c.status === "Returned"),
    submit: typed && isAuthor && c.status === "Draft",
    resubmit: typed && isAuthor && c.status === "Returned",
    close: !finished && ((isAuthor && ["Draft", "Submitted", "Returned"].includes(c.status)) || canCloseAny),
  };
}

export type StepState = "Done" | "Rejected" | "Cancelled" | "Overdue" | "Waiting";

/** Where one workflow step stands. A pending step past its due time is overdue; a reviewer's return is stored as a rejection of the step. */
export function stepState(step: Pick<HistoryStep, "status" | "dueAt">, now: Date): StepState {
  if (step.status === "Approved") return "Done";
  if (step.status === "Rejected" || step.status === "ChangesRequested") return "Rejected";
  if (step.status === "Cancelled") return "Cancelled";
  return new Date(step.dueAt).getTime() < now.getTime() ? "Overdue" : "Waiting";
}

/** Plain-language names for the audit actions shown in a letter's history: [English, Arabic]. */
export const EVENT_LABELS: Record<string, [string, string]> = {
  CorrespondenceCreated: ["Created", "أُنشئت"],
  CorrespondenceEdited: ["Edited by the author", "عدّلها المرسل"],
  CorrespondenceSubmitted: ["Sent", "أُرسلت"],
  CorrespondenceResubmitted: ["Sent again", "أُعيد إرسالها"],
  CorrespondenceClosed: ["Closed", "أُغلقت"],
  WorkflowStarted: ["Workflow started", "بدأ المسار"],
  WorkflowAdvanced: ["Moved to the next step", "انتقلت للخطوة التالية"],
  WorkflowStepSkipped: ["Step skipped", "تخطّي خطوة"],
  WorkflowReturned: ["Returned for changes", "أُعيدت للتعديل"],
  WorkflowRejected: ["Rejected", "رُفضت"],
  WorkflowCompleted: ["Completed", "اكتملت"],
  WorkflowClosed: ["Withdrawn or closed", "سُحبت أو أُغلقت"],
};

export const eventLabel = (action: string, ar: boolean) => EVENT_LABELS[action]?.[ar ? 1 : 0] ?? action;

/** The type's name in the page language. */
export const typeName = (t: Pick<TypeDef, "nameEn" | "nameAr">, ar: boolean) => (ar ? t.nameAr : t.nameEn);
