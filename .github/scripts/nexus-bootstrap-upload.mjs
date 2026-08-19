import fs from 'node:fs';
import path from 'node:path';

const apiBase = 'https://api.nexusmods.com/v3';
const apiKey = process.env.NEXUS_API_KEY;
const gameDomain = process.env.NEXUS_GAME_DOMAIN || 'mrprepper';
const gameScopedModId = process.env.NEXUS_GAME_SCOPED_MOD_ID;
const filename = process.env.RELEASE_ASSET;
const version = process.env.MOD_VERSION;
const name = process.env.MOD_DISPLAY_NAME;
const description = process.env.MOD_DESCRIPTION || '';

for (const [key, value] of Object.entries({ apiKey, gameScopedModId, filename, version, name })) {
  if (!value) throw new Error(`Missing required value: ${key}`);
}
if (!fs.existsSync(filename)) throw new Error(`Release asset not found: ${filename}`);

const api = async (url, options = {}) => {
  const response = await fetch(`${apiBase}${url}`, {
    ...options,
    headers: {
      apikey: apiKey,
      'Content-Type': 'application/json',
      'User-Agent': 'AcTePuKc/MrPrepper-Mods nexus bootstrap',
      ...(options.headers || {}),
    },
  });
  return response;
};

const expectJson = async (response, action) => {
  const text = await response.text();
  let body;
  try { body = text ? JSON.parse(text) : {}; } catch { body = { raw: text }; }
  if (!response.ok) throw new Error(`${action} failed (${response.status}): ${JSON.stringify(body)}`);
  return body;
};

// PRE-FLIGHT 1: resolve the site-visible mod ID to the v3 unique mod ID.
// This is intentionally done before creating any upload session.
const modResponse = await api(`/games/${encodeURIComponent(gameDomain)}/mods/${encodeURIComponent(gameScopedModId)}`);
if (!modResponse.ok) {
  const text = await modResponse.text();
  throw new Error(`Nexus mod preflight failed (${modResponse.status}). No upload was attempted. ${text}`);
}
const modBody = JSON.parse(await modResponse.text());
const modId = modBody?.data?.id;
if (!modId) throw new Error('Nexus mod preflight returned no unique mod id. No upload was attempted.');

// PRE-FLIGHT 2: bootstrap is allowed only when the mod page has NO existing files.
// If a previous run created one but failed later, this prevents duplicate files.
const filesBody = await expectJson(await api(`/mods/${encodeURIComponent(modId)}/files`), 'Check existing Nexus files');
const existingFiles = filesBody?.data?.mod_files || [];
if (existingFiles.length > 0) {
  const ids = existingFiles.map((file) => `${file.name || 'file'}=${file.id}`).join(', ');
  throw new Error(`Refusing bootstrap: this Nexus mod already has file(s): ${ids}. Add the correct NEXUS_FILE_ID secret and re-run through the normal update path.`);
}

const stat = fs.statSync(filename);
const createBody = await expectJson(await api('/uploads/multipart', {
  method: 'POST',
  body: JSON.stringify({ filename: path.basename(filename), size_bytes: String(stat.size) }),
}), 'Create Nexus upload session');

const upload = createBody.data;
if (!upload?.id || !Array.isArray(upload.part_presigned_urls) || !upload.complete_presigned_url) {
  throw new Error(`Unexpected upload-session response: ${JSON.stringify(createBody)}`);
}

const fd = fs.openSync(filename, 'r');
const parts = [];
try {
  for (let i = 0; i < upload.part_presigned_urls.length; i++) {
    const offset = i * upload.part_size_bytes;
    const remaining = Math.max(0, stat.size - offset);
    const length = Math.min(upload.part_size_bytes, remaining);
    const buffer = Buffer.alloc(length);
    fs.readSync(fd, buffer, 0, length, offset);
    const response = await fetch(upload.part_presigned_urls[i], {
      method: 'PUT',
      headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(length) },
      body: buffer,
    });
    if (!response.ok) throw new Error(`Upload part ${i + 1} failed (${response.status}): ${await response.text()}`);
    const etag = response.headers.get('etag');
    if (!etag) throw new Error(`Upload part ${i + 1} returned no ETag.`);
    parts.push({ partNumber: i + 1, etag: etag.replaceAll('"', '') });
  }
} finally {
  fs.closeSync(fd);
}

const xml = `<CompleteMultipartUpload>\n${parts.map((p) => `  <Part>\n    <PartNumber>${p.partNumber}</PartNumber>\n    <ETag>${p.etag}</ETag>\n  </Part>`).join('\n')}\n</CompleteMultipartUpload>`;
const completeResponse = await fetch(upload.complete_presigned_url, {
  method: 'POST',
  headers: { 'Content-Type': 'application/xml' },
  body: xml,
});
if (!completeResponse.ok) throw new Error(`Complete multipart upload failed (${completeResponse.status}): ${await completeResponse.text()}`);

await expectJson(await api(`/uploads/${upload.id}/finalise`, { method: 'POST' }), 'Finalise Nexus upload');

let available = false;
for (let attempt = 0; attempt < 60; attempt++) {
  const stateBody = await expectJson(await api(`/uploads/${upload.id}`), 'Check Nexus upload state');
  if (stateBody?.data?.state === 'available') {
    available = true;
    break;
  }
  await new Promise((resolve) => setTimeout(resolve, 2000));
}
if (!available) throw new Error('Nexus upload did not become available in time. Do not blindly re-run; inspect the Nexus mod page first.');

// This is the ONE mutating call that creates the first persistent Nexus file.
// There is deliberately no retry around it.
const fileBody = await expectJson(await api('/mod-files', {
  method: 'POST',
  body: JSON.stringify({
    upload_id: upload.id,
    mod_id: modId,
    name,
    description,
    version,
    file_category: 'main',
    primary_mod_manager_download: false,
    allow_mod_manager_download: true,
    show_requirements_pop_up: false,
    update_mod_version: true,
  }),
}), 'Create first Nexus mod file');

const created = fileBody?.data;
const createdFileId = created?.id || '(missing from response)';
console.log(`Created Nexus file id: ${createdFileId}`);

if (process.env.GITHUB_STEP_SUMMARY) {
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
    `## Nexus bootstrap upload\n\nCreated the first Nexus file successfully.\n\n- **File ID:** \`${createdFileId}\`\n- **Game-scoped file ID:** \`${created?.game_scoped_id || 'n/a'}\`\n- **Version:** \`${version}\`\n\nAdd the File ID as the matching \`NEXUS_FILE_ID_*\` repository secret before publishing the next version.\n`);
}
