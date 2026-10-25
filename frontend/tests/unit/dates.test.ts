import assert from "node:assert/strict";
import { test } from "node:test";
import { defaultMode, formatDate, hijriForInput } from "../../lib/dates";

const instant = "2026-10-24T22:30:00Z"; // 01:30 on 25 Oct in Riyadh
const tz = "Asia/Riyadh";
// ICU output differs slightly across Node versions (narrow no-break spaces, month abbreviations),
// so compare normalised whitespace and assert on day/year/era rather than exact month spellings.
const n = (s: string) => s.replace(/[\u00a0\u202f]/g, " ");

test("Gregorian only", () => {
  assert.equal(n(formatDate(instant, { mode: "gregorian", locale: "en", time: false, timeZone: tz })), "Oct 25, 2026");
});

test("Hijri only uses Umm al-Qura and the local day", () => {
  assert.match(n(formatDate(instant, { mode: "hijri", locale: "en", time: false, timeZone: tz })), /\b14, 1448 AH$/);
  assert.match(n(formatDate(instant, { mode: "hijri", locale: "en", time: false, timeZone: "UTC" })), /\b13, 1448 AH$/);
});

test("Both shows Gregorian then Hijri, time only once", () => {
  const both = n(formatDate(instant, { mode: "both", locale: "en", timeZone: tz }));
  assert.match(both, /^Oct 25, 2026, 1:30 AM · .*\b14, 1448 AH$/);
  assert.equal(both.match(/1:30/g)?.length, 1);
});

test("Arabic forces Gregorian when asked (ar-SA defaults to Hijri)", () => {
  const ar = formatDate(instant, { mode: "gregorian", locale: "ar", time: false, timeZone: tz });
  assert.match(ar, /٢٠٢٦/);
  assert.doesNotMatch(ar, /هـ/);
  assert.match(formatDate(instant, { mode: "hijri", locale: "ar", time: false, timeZone: tz }), /١٤٤٨ هـ/);
});

test("Empty and invalid values render a dash", () => {
  for (const v of [null, undefined, "", "not-a-date"]) assert.equal(formatDate(v, { mode: "both", locale: "en" }), "–");
});

test("Date inputs show the Hijri day without slipping to the previous day", () => {
  assert.equal(n(hijriForInput("2026-02-18", "en")), "Ramadan 1, 1447 AH");
  assert.equal(hijriForInput("", "en"), "");
});

test("Defaults: Arabic shows both calendars, English Gregorian", () => {
  assert.equal(defaultMode("ar"), "both");
  assert.equal(defaultMode("en"), "gregorian");
});
