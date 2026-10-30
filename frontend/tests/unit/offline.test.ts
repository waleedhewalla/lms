import assert from "node:assert/strict";
import { test } from "node:test";
import { discardQueue, enqueue, listQueue, replay, type QueueItem } from "../../lib/offlineQueue";
import { SNAPSHOT_MAX_AGE_MS, loadSnapshot, memoryStorage, purgeAll, purgeIfOtherPerson, saveSnapshot, type Snapshot } from "../../lib/offlineStore";

const now = new Date("2026-10-30T12:00:00Z");
const snap = (personId = "p1", savedAt = now.toISOString()): Snapshot => ({
  tenantId: "t1", personId, personName: "Layla", savedAt, counts: { approvals: 1, tasks: 1, overdue: 0 },
  approvals: [{ id: "a1", entityType: "Correspondence", dueAt: "2026-11-01T00:00:00Z", priority: "Normal" }],
  tasks: [{ id: "k1", title: "Report", status: "Open", dueAt: "2026-11-02T00:00:00Z", priority: "Normal", progress: 0 }],
});
const item = (kind: QueueItem["kind"], targetId: string, personId = "p1") => ({ kind, targetId, label: targetId, tenantId: "t1", personId });

test("the saved copy holds only the fields it declares (no tokens, no extra server fields)", async () => {
  const s = memoryStorage();
  const dirty = { ...snap(), token: "secret", approvals: [{ ...snap().approvals[0], comment: "private note", assigneeId: "x" }], tasks: [{ ...snap().tasks[0], description: "long text" }] };
  await saveSnapshot(s, dirty as unknown as Snapshot);
  const raw = JSON.stringify(await s.get("snapshot"));
  for (const leaked of ["secret", "private note", "assigneeId", "long text", "token"]) assert.ok(!raw.includes(leaked), `${leaked} must not be stored`);
  assert.equal((await loadSnapshot(s, now, "p1"))?.approvals[0].id, "a1");
});

test("a copy older than seven days is removed when read, and someone else's copy is not shown", async () => {
  const s = memoryStorage();
  await saveSnapshot(s, snap("p1", new Date(now.getTime() - SNAPSHOT_MAX_AGE_MS - 1000).toISOString()));
  await enqueue(s, item("approve", "a1"), now);
  assert.equal(await loadSnapshot(s, now, "p1"), undefined);
  assert.deepEqual(await s.keys(), []);   // the queue went with it

  await saveSnapshot(s, snap("p1"));
  assert.equal(await loadSnapshot(s, now, "p2"), undefined);
  assert.ok(await loadSnapshot(s, now, "p1"));
});

test("another person signing in removes the first person's copy and queue; the same person keeps them", async () => {
  const s = memoryStorage();
  await saveSnapshot(s, snap("p1"));
  await enqueue(s, item("approve", "a1"), now);
  assert.equal(await purgeIfOtherPerson(s, "p1"), false);
  assert.equal((await s.keys()).length, 2);
  assert.equal(await purgeIfOtherPerson(s, "p2"), true);
  assert.deepEqual(await s.keys(), []);
  // A queue with no copy still counts, and an unknown signed-in person (null) clears it.
  await enqueue(s, item("complete", "k1"), now);
  assert.equal(await purgeIfOtherPerson(s, null), true);
  assert.equal(await purgeIfOtherPerson(s, "p1"), false);   // nothing left to protect
});

test("purgeAll removes the copy and every queued action", async () => {
  const s = memoryStorage();
  await saveSnapshot(s, snap());
  await enqueue(s, item("reject", "a1"), now);
  await purgeAll(s);
  assert.deepEqual(await s.keys(), []);
});

test("the queue is kept in the order the actions were taken", async () => {
  const s = memoryStorage();
  await enqueue(s, item("approve", "second"), new Date("2026-10-30T12:00:02Z"), "id-b");
  await enqueue(s, item("approve", "first"), new Date("2026-10-30T12:00:01Z"), "id-a");
  assert.deepEqual((await listQueue(s)).map((q) => q.targetId), ["first", "second"]);
  await discardQueue(s);
  assert.deepEqual(await listQueue(s), []);
});

test("replay maps every server answer: sent, finished-with-a-reason, or kept for later", async () => {
  const s = memoryStorage();
  const answers: Record<string, number> = { ok: 200, done: 409, gone: 404, denied: 403, bad: 422 };
  for (const [i, t] of Object.keys(answers).entries()) await enqueue(s, item("approve", t), new Date(now.getTime() + i * 1000));
  const seen: string[] = [];
  const r = await replay(s, async (q) => { seen.push(q.targetId); return answers[q.targetId]; }, "p1");
  assert.deepEqual(seen, ["ok", "done", "gone", "denied", "bad"]);   // in order, each exactly once
  assert.deepEqual(r.sent.map((q) => q.targetId), ["ok"]);
  assert.deepEqual(r.dropped.map((d) => [d.item.targetId, d.reason]), [["done", "already-done"], ["gone", "not-found"], ["denied", "not-allowed"], ["bad", "rejected"]]);
  assert.equal(r.stoppedBy, null);
  assert.deepEqual(await listQueue(s), []);   // nothing is retried later
});

test("a 401 stops the run and keeps the rest; an unreachable server does the same", async () => {
  for (const [status, why] of [[401, "unauthorized"], [0, "unreachable"], [503, "unreachable"]] as const) {
    const s = memoryStorage();
    for (const [i, t] of ["one", "two", "three"].entries()) await enqueue(s, item("complete", t), new Date(now.getTime() + i * 1000));
    const r = await replay(s, async (q) => (q.targetId === "two" ? status : 200), "p1");
    assert.deepEqual(r.sent.map((q) => q.targetId), ["one"]);
    assert.deepEqual(r.kept.map((q) => q.targetId), ["two", "three"]);
    assert.equal(r.stoppedBy, why);
    assert.deepEqual((await listQueue(s)).map((q) => q.targetId), ["two", "three"]);   // still there for the next try
  }
});

test("an action queued by another person is never sent", async () => {
  const s = memoryStorage();
  await enqueue(s, item("approve", "theirs", "p2"), now);
  const sent: string[] = [];
  const r = await replay(s, async (q) => { sent.push(q.targetId); return 200; }, "p1");
  assert.deepEqual(sent, []);
  assert.equal(r.dropped[0].reason, "other-person");
  assert.deepEqual(await listQueue(s), []);
});

test("two replays at once send each action once", async () => {
  const s = memoryStorage();
  await enqueue(s, item("approve", "a1"), now);
  let calls = 0;
  const slow = async () => { calls++; await new Promise((r) => setTimeout(r, 20)); return 200; };
  const [x, y] = await Promise.all([replay(s, slow, "p1"), replay(s, slow, "p1")]);
  assert.equal(calls, 1);
  assert.deepEqual([x.stoppedBy, y.stoppedBy].sort(), ["busy", null]);
});
