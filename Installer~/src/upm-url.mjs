/**
 * Normalize a Unity UPM-style git URL into the native form accepted by `git clone`.
 *
 * UPM accepts URLs like:
 *   git+https://github.com/user/repo.git
 *   git+ssh://git@github.com/user/repo.git#feature-branch
 *   git+file:///path/to/repo#v1.2.3
 *   https://github.com/user/repo.git?path=Packages/Sub#main
 *
 * Native `git clone` cannot parse the `git+` scheme prefix (it treats it as a
 * remote helper name), ignores `#fragment` (must be `--branch <ref>`), and does
 * not know about UPM's `?path=` query.
 *
 * Schemes are restricted to a known-safe allowlist so a malicious or typo'd
 * manifest entry cannot ask git to use a remote helper for an arbitrary scheme
 * (smart-http remote helpers can execute attacker-controlled binaries during
 * clone). `file://` is permitted because UPM legitimately supports local clones,
 * but absolute paths must still be visible to the user.
 *
 * @param {string} upmUrl UPM-formatted dependency URL.
 * @returns {{ url: string, ref: string | null }} Native git URL and optional ref.
 */
export function parseUpmUrl(upmUrl) {
  if (typeof upmUrl !== "string" || upmUrl.length === 0) {
    throw new Error("parseUpmUrl: empty url");
  }

  // Reject control characters and newlines outright — these have no business in
  // any URL and are a classic vector for argument-injection into git.
  if (/[\x00-\x1F\x7F]/.test(upmUrl)) {
    throw new Error("parseUpmUrl: url contains control characters");
  }

  // Hard upper bound to avoid pathological inputs.
  if (upmUrl.length > 2048) {
    throw new Error("parseUpmUrl: url exceeds 2048 characters");
  }

  let url = upmUrl.startsWith("git+") ? upmUrl.slice(4) : upmUrl;

  let ref = null;
  const hashIdx = url.indexOf("#");
  if (hashIdx >= 0) {
    ref = url.slice(hashIdx + 1) || null;
    url = url.slice(0, hashIdx);
  }

  const qIdx = url.indexOf("?");
  if (qIdx >= 0) {
    url = url.slice(0, qIdx);
  }

  // Allowlist of git transport schemes. `git clone` will happily load remote
  // helpers (e.g. `ext::`, `transport-helper::`, `helper::script`) that can run
  // arbitrary code; reject anything outside the known-safe set.
  const schemeMatch = /^([a-zA-Z][a-zA-Z0-9+.\-]*):/.exec(url);
  const sshShorthand = /^[a-zA-Z0-9_.\-]+@[a-zA-Z0-9_.\-]+:/.test(url);

  if (schemeMatch) {
    const scheme = schemeMatch[1].toLowerCase();
    const allowed = new Set(["http", "https", "ssh", "git", "file"]);
    if (!allowed.has(scheme)) {
      throw new Error(`parseUpmUrl: scheme '${scheme}' is not allowed`);
    }
  } else if (!sshShorthand) {
    throw new Error("parseUpmUrl: url must include a scheme or be SSH shorthand (user@host:path)");
  }

  // `--upload-pack`-style options would smuggle args into git; reject any URL
  // that begins with `-` after stripping `git+`.
  if (url.startsWith("-")) {
    throw new Error("parseUpmUrl: url must not start with '-'");
  }

  // Refs feed into `--branch <ref>`; refuse leading dashes there too so a ref
  // value can never be parsed as another option.
  if (ref !== null && (ref.startsWith("-") || /[\x00-\x1F\x7F\s]/.test(ref))) {
    throw new Error("parseUpmUrl: ref contains unsafe characters");
  }

  return { url, ref };
}
