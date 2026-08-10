/**
 * Sets one rule's severity inside a `.archon.json` document by editing the smallest span of text
 * that has to change, so a comment or an unrelated key elsewhere in the file survives untouched —
 * a full parse-and-restringify would silently drop both, since `.archon.json` permits comments
 * and trailing commas that `JSON.parse` does not.
 *
 * Returns `undefined` when the document's shape cannot be trusted enough to edit safely (a
 * non-object root, or a `rules` value that is not itself an object). The caller's job in that
 * case is to say so and point at the file, not to guess.
 */
export function upsertRuleSeverity(text: string, ruleId: string, severity: string): string | undefined {
  const rootOpen = skipTrivia(text, 0);
  if (text[rootOpen] !== '{') {
    return undefined;
  }
  const rootClose = skipBalanced(text, rootOpen, '{', '}');
  if (rootClose === undefined) {
    return undefined;
  }
  const rootProperties = scanProperties(text, rootOpen + 1, rootClose);
  if (rootProperties === undefined) {
    return undefined;
  }

  const rulesProperty = rootProperties.find((p) => p.key === 'rules');
  if (rulesProperty === undefined) {
    return insertRulesBlock(text, rootOpen, rootProperties.length > 0, ruleId, severity);
  }

  const rulesOpen = skipTrivia(text, rulesProperty.valueStart);
  if (text[rulesOpen] !== '{') {
    return undefined;
  }
  const rulesClose = skipBalanced(text, rulesOpen, '{', '}');
  if (rulesClose === undefined) {
    return undefined;
  }
  const ruleProperties = scanProperties(text, rulesOpen + 1, rulesClose);
  if (ruleProperties === undefined) {
    return undefined;
  }

  const existing = ruleProperties.find((p) => p.key.toUpperCase() === ruleId.toUpperCase());
  if (existing) {
    return text.slice(0, existing.valueStart) + JSON.stringify(severity) + text.slice(existing.valueEnd);
  }
  return (
    text.slice(0, rulesOpen + 1) +
    propertyInsertion(ruleId, severity, ruleProperties.length > 0, '    ') +
    text.slice(rulesOpen + 1)
  );
}

function insertRulesBlock(text: string, rootOpen: number, hasSiblings: boolean, ruleId: string, severity: string): string {
  const block = `\n  "rules": {\n    ${JSON.stringify(ruleId)}: ${JSON.stringify(severity)}\n  }`;
  return text.slice(0, rootOpen + 1) + (hasSiblings ? `${block},` : `${block}\n`) + text.slice(rootOpen + 1);
}

function propertyInsertion(key: string, value: string, hasSiblings: boolean, indent: string): string {
  const entry = `\n${indent}${JSON.stringify(key)}: ${JSON.stringify(value)}`;
  return hasSiblings ? `${entry},` : `${entry}\n`;
}

interface JsonProperty {
  key: string;
  valueStart: number;
  valueEnd: number;
}

/**
 * Reads the top-level properties of an object body, from just after its `{` to its `}` (exclusive
 * of both braces). Returns `undefined` on anything that does not look like a well-formed sequence
 * of `"key": value` pairs — a caller must treat that as "cannot edit", never as "empty".
 */
function scanProperties(text: string, bodyStart: number, bodyEnd: number): JsonProperty[] | undefined {
  const properties: JsonProperty[] = [];
  let i = skipTrivia(text, bodyStart);

  while (i < bodyEnd) {
    if (text[i] !== '"') {
      return undefined;
    }
    const keyEnd = skipString(text, i);
    if (keyEnd === undefined) {
      return undefined;
    }
    const key = JSON.parse(text.slice(i, keyEnd)) as string;

    i = skipTrivia(text, keyEnd);
    if (text[i] !== ':') {
      return undefined;
    }
    const valueStart = skipTrivia(text, i + 1);
    const valueEnd = skipValue(text, valueStart);
    if (valueEnd === undefined) {
      return undefined;
    }
    properties.push({ key, valueStart, valueEnd });

    i = skipTrivia(text, valueEnd);
    if (text[i] === ',') {
      i = skipTrivia(text, i + 1);
      continue;
    }
    if (i === bodyEnd) {
      break;
    }
    return undefined;
  }
  return properties;
}

/** Skips one JSON value — string, number, literal, object or array — returning the index past it. */
function skipValue(text: string, i: number): number | undefined {
  const c = text[i];
  if (c === '"') {
    return skipString(text, i);
  }
  if (c === '{') {
    const close = skipBalanced(text, i, '{', '}');
    return close === undefined ? undefined : close + 1;
  }
  if (c === '[') {
    const close = skipBalanced(text, i, '[', ']');
    return close === undefined ? undefined : close + 1;
  }
  // A bare literal — number, true, false or null — ends at the next structural character.
  let end = i;
  while (end < text.length && !',}]/\t\r\n '.includes(text[end])) {
    end++;
  }
  return end > i ? end : undefined;
}

/** Skips a `"..."` string starting at `i`, respecting backslash escapes. */
function skipString(text: string, i: number): number | undefined {
  let j = i + 1;
  while (j < text.length) {
    if (text[j] === '\\') {
      j += 2;
      continue;
    }
    if (text[j] === '"') {
      return j + 1;
    }
    j++;
  }
  return undefined;
}

/**
 * Walks from an opening bracket to its match, respecting strings and comments along the way so a
 * brace inside either is never mistaken for a structural one. Returns the index of the matching
 * close bracket, or `undefined` for an unterminated one.
 */
function skipBalanced(text: string, openIndex: number, open: string, close: string): number | undefined {
  let depth = 0;
  let i = openIndex;
  while (i < text.length) {
    const c = text[i];
    if (c === '"') {
      const end = skipString(text, i);
      if (end === undefined) {
        return undefined;
      }
      i = end;
      continue;
    }
    if (c === '/' && text[i + 1] === '/') {
      const newline = text.indexOf('\n', i);
      i = newline === -1 ? text.length : newline + 1;
      continue;
    }
    if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2);
      i = end === -1 ? text.length : end + 2;
      continue;
    }
    if (c === open) {
      depth++;
      i++;
      continue;
    }
    if (c === close) {
      depth--;
      i++;
      if (depth === 0) {
        return i - 1;
      }
      continue;
    }
    i++;
  }
  return undefined;
}

/** Advances past whitespace and `//`/`/* *\/` comments, both of which `.archon.json` permits. */
function skipTrivia(text: string, i: number): number {
  while (i < text.length) {
    const c = text[i];
    if (c === ' ' || c === '\t' || c === '\r' || c === '\n') {
      i++;
      continue;
    }
    if (c === '/' && text[i + 1] === '/') {
      const newline = text.indexOf('\n', i);
      i = newline === -1 ? text.length : newline + 1;
      continue;
    }
    if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2);
      i = end === -1 ? text.length : end + 2;
      continue;
    }
    break;
  }
  return i;
}
