import assert from "node:assert/strict";
import { test } from "node:test";
import { pageWindow, recipientsOf, registerQuery, replyDirection, replySubject } from "../../lib/correspondence";
import { personIdOf } from "../../lib/modules";

test("registerQuery keeps only the filters that are set", () => {
  assert.equal(registerQuery({ tenantId: "t1" }), "tenantId=t1");
  const q = new URLSearchParams(registerQuery({ tenantId: "t1", year: 2026, type: "Incoming", status: "", q: "  budget & plan ", skip: 50, take: 25 }, { calendar: "both" }));
  assert.equal(q.get("year"), "2026");
  assert.equal(q.get("type"), "Incoming");
  assert.equal(q.get("status"), null);
  assert.equal(q.get("q"), "budget & plan");   // trimmed, and the ampersand survives encoding
  assert.equal(q.get("skip"), "50");
  assert.equal(q.get("calendar"), "both");
  assert.equal(new URLSearchParams(registerQuery({ tenantId: "t1", skip: 0 })).get("skip"), null);
  assert.equal(new URLSearchParams(registerQuery({ tenantId: "t1", year: Number.NaN })).get("year"), null);
});

test("recipients are split into To and Cc", () => {
  const row = { recipients: [
    { displayName: "Ministry", isExternal: true, kind: "To" as const },
    { displayName: "Legal Office", isExternal: false, kind: "Cc" as const },
  ] };
  assert.deepEqual(recipientsOf(row, "To"), ["Ministry"]);
  assert.deepEqual(recipientsOf(row, "Cc"), ["Legal Office"]);
});

test("a reply keeps one Re: and answers an incoming letter with an outgoing one", () => {
  assert.equal(replySubject("Budget"), "Re: Budget");
  assert.equal(replySubject("Re: Budget"), "Re: Budget");
  assert.equal(replySubject("RE: Budget"), "RE: Budget");
  assert.equal(replyDirection("Incoming"), "Outgoing");
  assert.equal(replyDirection("Internal"), "Internal");
  assert.equal(replyDirection("Outgoing"), "Outgoing");
});

test("page window labels and buttons", () => {
  assert.deepEqual(pageWindow({ total: 0, skip: 0, take: 50 }), { from: 0, to: 0, hasPrev: false, hasNext: false });
  assert.deepEqual(pageWindow({ total: 120, skip: 0, take: 50 }), { from: 1, to: 50, hasPrev: false, hasNext: true });
  assert.deepEqual(pageWindow({ total: 120, skip: 100, take: 50 }), { from: 101, to: 120, hasPrev: true, hasNext: false });
});

test("the acting person is the person_id claim, only when it is a GUID", () => {
  const id = "3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b";
  assert.equal(personIdOf({ person_id: id }), id);
  assert.equal(personIdOf({ person_id: "not-a-guid" }), null);
  assert.equal(personIdOf({}), null);
  assert.equal(personIdOf(null), null);
});
