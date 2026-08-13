import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { unzipSync, zipSync } from 'fflate';

const DEFAULT_MAX_ARCHIVE_BYTES = 256 * 1024 * 1024;
const archiveMagic = [
  Buffer.from([0x50, 0x4b, 0x03, 0x04]),
  Buffer.from([0x50, 0x4b, 0x05, 0x06]),
  Buffer.from([0x50, 0x4b, 0x07, 0x08]),
];
const credentialPatterns = [
  {
    label: 'JWT',
    pattern: /\beyJ[A-Za-z0-9_-]{2,2048}\.[A-Za-z0-9_-]{2,8192}\.[A-Za-z0-9_-]{16,1024}\b/g,
  },
  {
    label: 'PASSWORD',
    pattern: /Promptly-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-A1/g,
  },
];
const htmlArchivePrefix = '<template id="playwrightReportBase64">data:application/zip;base64,';
const htmlArchiveSuffix = '</template>';
const runErrorCategoryOrder = [
  'verification',
  'diagnostics',
  'provider diagnostics',
  'cleanup',
  'cleanup evidence',
  'termination',
  'runner log',
  'artifact sanitization',
  'artifact verification',
  'artifact safety',
];

export const resolveFixedE2EPaths = (moduleUrl) => {
  const e2eRoot = path.dirname(fileURLToPath(moduleUrl));
  const webRoot = path.dirname(e2eRoot);
  const repositoryRoot = path.resolve(webRoot, '../../..');
  return Object.freeze({
    artifactsRoot: path.join(repositoryRoot, 'artifacts/test-results/e2e'),
    e2eRoot,
    repositoryRoot,
    webRoot,
  });
};

export const createSafeRunMetadata = ({
  completedAt,
  errorCategories,
  errorCount,
  startedAt,
}) => {
  if (!(startedAt instanceof Date) || Number.isNaN(startedAt.valueOf())) {
    throw new Error('Run metadata requires a valid start time');
  }
  if (!(completedAt instanceof Date) || Number.isNaN(completedAt.valueOf())) {
    throw new Error('Run metadata requires a valid completion time');
  }
  if (!Number.isSafeInteger(errorCount) || errorCount < 0) {
    throw new Error('Run metadata requires a nonnegative integer error count');
  }

  const requestedCategories = new Set(
    Array.isArray(errorCategories) ? errorCategories : [],
  );
  const safeCategories = runErrorCategoryOrder.filter((category) => (
    requestedCategories.has(category)
  ));
  if ([...requestedCategories].some((category) => !runErrorCategoryOrder.includes(category))) {
    safeCategories.push('unclassified');
  }

  return {
    schema: 1,
    startedAt: startedAt.toISOString(),
    completedAt: completedAt.toISOString(),
    status: errorCount === 0 ? 'passed' : 'failed',
    errorCount,
    errorCategories: safeCategories,
  };
};

const equalLengthMask = (label, length) => {
  const marker = `[REDACTED-${label}]`;
  return marker.slice(0, length).padEnd(length, '*');
};

const isArchive = (name, content) => (
  name.toLowerCase().endsWith('.zip')
  || archiveMagic.some((magic) => content.subarray(0, magic.length).equals(magic))
);

export const createArtifactSanitizer = (
  knownSecrets,
  { maxArchiveBytes = DEFAULT_MAX_ARCHIVE_BYTES } = {},
) => {
  const secrets = [...new Set(knownSecrets.filter((secret) => (
    typeof secret === 'string' && secret.length > 0
  )))];

  const sanitizeBuffer = (input) => {
    const original = Buffer.from(input);
    let text = original.toString('latin1');
    for (const secret of secrets) {
      text = text.replaceAll(secret, equalLengthMask('SECRET', secret.length));
    }
    for (const { label, pattern } of credentialPatterns) {
      text = text.replace(pattern, (credential) => equalLengthMask(label, credential.length));
    }
    const content = Buffer.from(text, 'latin1');
    return { changed: !content.equals(original), content };
  };

  const expandArchive = (input) => {
    const archive = Buffer.from(input);
    if (archive.length > maxArchiveBytes) {
      throw new Error(`Compressed artifact exceeds ${maxArchiveBytes} bytes`);
    }
    let expandedBytes = 0;
    const entryNames = new Set();
    const entries = unzipSync(archive, {
      filter: (entry) => {
        if (entryNames.has(entry.name)) {
          throw new Error(`Compressed artifact contains a duplicate entry: ${entry.name}`);
        }
        entryNames.add(entry.name);
        expandedBytes += entry.originalSize;
        if (expandedBytes > maxArchiveBytes) {
          throw new Error(`Expanded artifact exceeds ${maxArchiveBytes} bytes`);
        }
        return true;
      },
    });
    if (
      Object.keys(entries).length !== entryNames.size
      || [...entryNames].some((name) => !Object.hasOwn(entries, name))
    ) {
      throw new Error('Compressed artifact entry inventory is not safely representable');
    }
    return entries;
  };

  const inspectEntries = (entries, sanitize) => {
    const inspected = {};
    let changed = false;
    for (const [name, entry] of Object.entries(entries)) {
      const content = Buffer.from(entry);
      if (isArchive(name, content)) {
        throw new Error(`Nested archive is not upload-safe: ${name}`);
      }
      const result = sanitizeBuffer(content);
      if (!sanitize && result.changed) {
        throw new Error(`Compressed artifact contains credential material: ${name}`);
      }
      inspected[name] = result.content;
      changed ||= result.changed;
    }
    return { changed, entries: inspected };
  };

  const sanitizeArchive = (input) => {
    const archive = Buffer.from(input);
    const inspected = inspectEntries(expandArchive(archive), true);
    if (!inspected.changed) {
      return { changed: false, content: archive };
    }
    const content = Buffer.from(zipSync(inspected.entries, { level: 6 }));
    if (content.length > maxArchiveBytes) {
      throw new Error(`Sanitized artifact exceeds ${maxArchiveBytes} bytes`);
    }
    inspectEntries(expandArchive(content), false);
    return { changed: true, content };
  };

  const transformHtmlReport = (input, sanitize) => {
    const html = Buffer.from(input).toString('utf8');
    const prefixIndex = html.indexOf(htmlArchivePrefix);
    if (prefixIndex < 0) {
      throw new Error('Playwright HTML report does not contain its embedded archive');
    }
    const archiveStart = prefixIndex + htmlArchivePrefix.length;
    const archiveEnd = html.indexOf(htmlArchiveSuffix, archiveStart);
    if (archiveEnd < 0 || html.indexOf(htmlArchivePrefix, archiveStart) >= 0) {
      throw new Error('Playwright HTML report has an invalid embedded-archive contract');
    }
    const encodedArchive = html.slice(archiveStart, archiveEnd);
    if (!/^[A-Za-z0-9+/]+=*$/.test(encodedArchive)) {
      throw new Error('Playwright HTML report has invalid base64 archive data');
    }
    const archive = Buffer.from(encodedArchive, 'base64');
    const canonicalArchive = archive.toString('base64');
    if (canonicalArchive !== encodedArchive) {
      throw new Error('Playwright HTML report has noncanonical base64 archive data');
    }
    const outsidePrefix = Buffer.from(html.slice(0, archiveStart));
    const outsideSuffix = Buffer.from(html.slice(archiveEnd));

    if (!sanitize) {
      assertBufferSafe(outsidePrefix);
      assertBufferSafe(outsideSuffix);
      assertArchiveSafe(archive);
      return { changed: false, content: Buffer.from(html) };
    }
    const sanitizedArchive = sanitizeArchive(archive);
    const sanitizedPrefix = sanitizeBuffer(outsidePrefix);
    const sanitizedSuffix = sanitizeBuffer(outsideSuffix);
    if (!sanitizedArchive.changed && !sanitizedPrefix.changed && !sanitizedSuffix.changed) {
      return { changed: false, content: Buffer.from(html) };
    }
    const content = Buffer.from(
      `${sanitizedPrefix.content.toString('utf8')}${sanitizedArchive.content.toString('base64')}`
      + sanitizedSuffix.content.toString('utf8'),
    );
    transformHtmlReport(content, false);
    return { changed: true, content };
  };

  const assertBufferSafe = (input) => {
    if (sanitizeBuffer(input).changed) {
      throw new Error('Artifact contains credential material');
    }
  };

  const assertArchiveSafe = (input) => {
    inspectEntries(expandArchive(input), false);
  };

  return {
    assertArchiveSafe,
    assertBufferSafe,
    assertHtmlReportSafe: (input) => transformHtmlReport(input, false),
    sanitizeArchive,
    sanitizeBuffer,
    sanitizeHtmlReport: (input) => transformHtmlReport(input, true),
  };
};

export const assertNoDefaultInternetRoute = ({
  canary,
  control,
  ipv4Routes,
  ipv6Routes,
  service,
}) => {
  if (canary.code !== 0) {
    throw new Error(`${service} routed egress canary failed with code ${canary.code}`);
  }
  if (control.code !== 0) {
    throw new Error(`${service} egress control probe failed with code ${control.code}`);
  }
  if (ipv4Routes.code !== 0 || ipv4Routes.stderr.trim() !== '') {
    throw new Error(`${service} IPv4 route table could not be read exactly`);
  }
  const ipv4Lines = ipv4Routes.stdout.trim().split('\n').filter(Boolean);
  if (ipv4Lines.length < 2 || !/^Iface\s+Destination\s+Gateway\s+Flags\b/.test(ipv4Lines[0])) {
    throw new Error(`${service} IPv4 route table has an invalid format`);
  }
  const ipv4Entries = ipv4Lines.slice(1).map((line) => line.trim().split(/\s+/));
  if (ipv4Entries.some((entry) => (
    entry.length !== 11
    || !/^[0-9A-Fa-f]{8}$/.test(entry[1])
    || !/^[0-9A-Fa-f]{8}$/.test(entry[2])
    || !/^[0-9A-Fa-f]{4}$/.test(entry[3])
    || !entry.slice(4, 7).every((value) => /^\d+$/.test(value))
    || !/^[0-9A-Fa-f]{8}$/.test(entry[7])
    || !entry.slice(8).every((value) => /^\d+$/.test(value))
  ))) {
    throw new Error(`${service} IPv4 route table contains an invalid entry`);
  }
  if (ipv4Entries.some(([iface, destination, , flags, , , , mask]) => (
    iface !== 'lo'
    && destination === '00000000'
    && mask === '00000000'
    && (Number.parseInt(flags, 16) & 1) === 1
  ))) {
    throw new Error(`${service} has an active default IPv4 route`);
  }

  if (ipv6Routes.code !== 0 || ipv6Routes.stderr.trim() !== '') {
    throw new Error(`${service} IPv6 route table could not be read exactly`);
  }
  const ipv6Entries = ipv6Routes.stdout.trim().split('\n').filter(Boolean)
    .map((line) => line.trim().split(/\s+/));
  if (ipv6Entries.some((entry) => (
    entry.length !== 10
    || !/^[0-9A-Fa-f]{32}$/.test(entry[0])
    || !/^[0-9A-Fa-f]{2}$/.test(entry[1])
    || !/^[0-9A-Fa-f]{32}$/.test(entry[2])
    || !/^[0-9A-Fa-f]{2}$/.test(entry[3])
    || !/^[0-9A-Fa-f]{32}$/.test(entry[4])
    || !entry.slice(5, 9).every((value) => /^[0-9A-Fa-f]{8}$/.test(value))
  ))) {
    throw new Error(`${service} IPv6 route table contains an invalid entry`);
  }
  if (ipv6Entries.some((entry) => (
    entry[0] === '00000000000000000000000000000000'
    && entry[1] === '00'
    && entry[9] !== 'lo'
  ))) {
    throw new Error(`${service} has a default IPv6 route`);
  }
};
