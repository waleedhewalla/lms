import assert from "node:assert/strict";
import { test } from "node:test";
import { HELP, SYSTEMS, canCreate, errorText, isLive, optionsFor } from "../../lib/connectors";

test("only a file connector is not live, and it has no help for credentials", () => {
  assert.deepEqual(SYSTEMS.filter((s) => !isLive(s)), ["Csv"]);
  assert.equal(HELP.Csv, null);
  for (const s of SYSTEMS.filter(isLive)) assert.ok(HELP[s]!.credential.en.length > 0 && HELP[s]!.credential.ar.length > 0);
});

test("options keep only what the system accepts, filled in, trimmed", () => {
  assert.deepEqual(optionsFor("Canvas", { accountId: " 7 ", sessionCookie: "x", siteType: "" }), { accountId: "7" });
  assert.deepEqual(optionsFor("Sakai", { siteType: "course", sessionCookie: "  ", accountId: "7" }), { siteType: "course" });
  assert.deepEqual(optionsFor("Moodle", { accountId: "7" }), {});
  assert.deepEqual(optionsFor("Csv", { accountId: "7" }), {});
});

test("the form can be sent only when the system's needs are met", () => {
  assert.equal(canCreate("Moodle", "Campus", "https://m.example.edu", "token"), true);
  assert.equal(canCreate("Moodle", "Campus", "", "token"), false);
  assert.equal(canCreate("Moodle", "Campus", "https://m.example.edu", ""), false);
  assert.equal(canCreate("Moodle", "  ", "https://m.example.edu", "token"), false);
  assert.equal(canCreate("Csv", "Banner export", "", ""), true);
  assert.equal(canCreate("Csv", "x".repeat(101), "", ""), false);
});

test("server messages are shown as sent, with the file problems appended", () => {
  assert.equal(errorText({ error: "Canvas: the server refused the credential (401)." }, "fallback"), "Canvas: the server refused the credential (401).");
  assert.equal(errorText({ error: "The file was not imported.", problems: ["Row 3: code and title are required."], more: 2 }, "f"),
    "The file was not imported. Row 3: code and title are required. (and 2 more)");
  assert.equal(errorText(null, "fallback"), "fallback");
  assert.equal(errorText({ nothing: true }, "fallback"), "fallback");
});
