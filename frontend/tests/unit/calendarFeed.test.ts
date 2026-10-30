import assert from "node:assert/strict";
import { test } from "node:test";
import { feedUrl, isInsecure, webcalUrl } from "../../lib/calendarFeed";

test("feed address joins the API base and the returned path once", () => {
  assert.equal(feedUrl("https://api.example.edu", "/api/calendar/feed/abc.ics"), "https://api.example.edu/api/calendar/feed/abc.ics");
  assert.equal(feedUrl("https://api.example.edu/", "/api/calendar/feed/abc.ics"), "https://api.example.edu/api/calendar/feed/abc.ics");
});

test("webcal links use webcals for https and webcal for http", () => {
  assert.equal(webcalUrl("https://api.example.edu/x.ics"), "webcals://api.example.edu/x.ics");
  assert.equal(webcalUrl("http://localhost:5238/x.ics"), "webcal://localhost:5238/x.ics");
});

test("only plain http is flagged as insecure", () => {
  assert.equal(isInsecure("http://localhost:5238/x.ics"), true);
  assert.equal(isInsecure("https://api.example.edu/x.ics"), false);
});
