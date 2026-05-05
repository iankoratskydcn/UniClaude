import { test } from "node:test";
import { strict as assert } from "node:assert";
import { parseUpmUrl } from "../src/upm-url.mjs";

test("parseUpmUrl: plain https, no fragment", () => {
  const r = parseUpmUrl("https://github.com/user/repo.git");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: git+https strips prefix", () => {
  const r = parseUpmUrl("git+https://github.com/user/repo.git");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: git+ssh strips prefix", () => {
  const r = parseUpmUrl("git+ssh://git@github.com/user/repo.git");
  assert.equal(r.url, "ssh://git@github.com/user/repo.git");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: git+file strips prefix", () => {
  const r = parseUpmUrl("git+file:///Users/me/Projects/Repo");
  assert.equal(r.url, "file:///Users/me/Projects/Repo");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: branch fragment splits off", () => {
  const r = parseUpmUrl("git+https://github.com/user/repo.git#feature/ninja-mode");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, "feature/ninja-mode");
});

test("parseUpmUrl: tag fragment splits off", () => {
  const r = parseUpmUrl("https://github.com/user/repo.git#v1.2.3");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, "v1.2.3");
});

test("parseUpmUrl: commit sha fragment splits off", () => {
  const r = parseUpmUrl("git+https://github.com/user/repo.git#abc1234");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, "abc1234");
});

test("parseUpmUrl: file:// with branch fragment (the actual production failure)", () => {
  const r = parseUpmUrl(
    "git+file:///Users/mike/Projects/ArcForge/Packages/com.arcforge.uniclaude#feature/ninja-mode"
  );
  assert.equal(r.url, "file:///Users/mike/Projects/ArcForge/Packages/com.arcforge.uniclaude");
  assert.equal(r.ref, "feature/ninja-mode");
});

test("parseUpmUrl: ?path= query is stripped", () => {
  const r = parseUpmUrl("https://github.com/user/repo.git?path=Packages/Sub");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: ?path= combined with #ref", () => {
  const r = parseUpmUrl("git+https://github.com/user/repo.git?path=Packages/Sub#main");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, "main");
});

test("parseUpmUrl: trailing empty fragment yields null ref", () => {
  const r = parseUpmUrl("https://github.com/user/repo.git#");
  assert.equal(r.url, "https://github.com/user/repo.git");
  assert.equal(r.ref, null);
});

test("parseUpmUrl: empty string throws", () => {
  assert.throws(() => parseUpmUrl(""), /empty url/);
});

test("parseUpmUrl: non-string throws", () => {
  assert.throws(() => parseUpmUrl(null), /empty url/);
  assert.throws(() => parseUpmUrl(undefined), /empty url/);
});

test("parseUpmUrl: rejects unknown schemes", () => {
  assert.throws(() => parseUpmUrl("ext::sh foo"), /scheme 'ext' is not allowed/);
  assert.throws(() => parseUpmUrl("git+ext::sh foo"), /scheme 'ext' is not allowed/);
  assert.throws(() => parseUpmUrl("javascript:alert(1)"), /scheme 'javascript' is not allowed/);
});

test("parseUpmUrl: rejects control characters", () => {
  assert.throws(() => parseUpmUrl("https://example.com/repo\nrm -rf"), /control characters/);
  assert.throws(() => parseUpmUrl("https://example.com/\x00"), /control characters/);
});

test("parseUpmUrl: rejects URL starting with dash (would be parsed as a git option)", () => {
  assert.throws(() => parseUpmUrl("--upload-pack=evil"), /must include a scheme/);
});

test("parseUpmUrl: rejects ref containing whitespace or leading dash", () => {
  assert.throws(
    () => parseUpmUrl("https://example.com/repo.git#--evil"),
    /ref contains unsafe characters/
  );
  assert.throws(
    () => parseUpmUrl("https://example.com/repo.git#has space"),
    /ref contains unsafe characters/
  );
});

test("parseUpmUrl: rejects URL exceeding 2048 chars", () => {
  const huge = "https://example.com/" + "a".repeat(3000);
  assert.throws(() => parseUpmUrl(huge), /exceeds 2048/);
});

test("parseUpmUrl: accepts SSH shorthand (user@host:path)", () => {
  const r = parseUpmUrl("git@github.com:user/repo.git");
  assert.equal(r.url, "git@github.com:user/repo.git");
  assert.equal(r.ref, null);
});
