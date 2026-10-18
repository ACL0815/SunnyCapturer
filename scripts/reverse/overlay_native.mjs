// Private, persistent REA stdio session; never calls the root/shared MCP server.
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFile, mkdir, readFile, readdir, realpath, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { createInterface } from 'node:readline';

const workspace = resolve(import.meta.dirname, '../..');
const privateRoot = resolve(process.env.NATIVE_DEEP_PRIVATE_ROOT ?? 'C:/Users/Axel_desktop/AppData/Local/KairosCapture/ReverseTools/overlay-contract-20261018');
const output = join(workspace, 'docs/kairos/reverse/overlay-native');
const launcher = process.env.NATIVE_DEEP_REA ?? 'C:/Users/Axel_desktop/.t3/projects/clockify/.rea-runtime/node_modules/rea-agents/scripts/rea.mjs';
const allowed = new Set(['open_binary', 'close_binary', 'binary_session', 'binary_overview', 'search_procedures', 'search_strings', 'list_names', 'xrefs', 'resolve_containing_procedure', 'analyze_function', 'procedure_pseudo_code', 'procedure_callers', 'procedure_callees', 'procedure_references', 'read_bytes', 'inspect_address_context', 'inspect_analysis_view', 'export_evidence_bundle', 'get_evidence_bundle', 'address_to_file_offset', 'find_xrefs_to_name', 'inspect_native_dispatch_metadata']);
const authorizedRoot = resolve('C:/Users/Axel_desktop/AppData/Local/KairosCapture/ReverseTools/overlay-contract-20261018');
if (privateRoot !== authorizedRoot) throw new Error('Private root differs from the explicitly authorized runtime directory.');
for (const directory of [privateRoot, join(privateRoot, 'inbox'), join(privateRoot, 'done'), join(output, 'evidence')]) await mkdir(directory, { recursive: true });
// Explicit scope correction authorizes this ordinary resolved path. Never use
// short names, junctions, mappings or aliases to work around admission rules.
if ((await realpath(privateRoot)).toLowerCase() !== authorizedRoot.toLowerCase()) throw new Error('Runtime path resolves elsewhere.');
const env = { ...process.env, TEMP: privateRoot, TMP: privateRoot, REA_ANALYSIS_PROVIDER: 'ghidra' };
if (!env.GHIDRA_INSTALL_DIR || !env.JAVA_HOME) throw new Error('Process-local GHIDRA_INSTALL_DIR and JAVA_HOME are required.');
const target = 'C:/Users/Axel_desktop/AppData/Local/Programs/SunnyCapturer/SunnyCapturer.exe';
if (createHash('sha256').update(await readFile(target)).digest('hex') !== 'e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8') throw new Error('Target digest mismatch.');
const child = spawn(process.execPath, [launcher, '--mcp'], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
child.stderr.on('data', data => { void appendFile(join(privateRoot, 'rea-stderr.log'), data); });
const pending = new Map();
let sequence = 0;
let closed = false;
const failures = new Map();
createInterface({ input: child.stdout }).on('line', line => {
  try {
    const message = JSON.parse(line);
    const request = pending.get(message.id);
    if (request) {
      pending.delete(message.id);
      if (message.error) request.reject(new Error(JSON.stringify(message.error)));
      else request.resolve(message.result);
    }
  } catch { void appendFile(join(privateRoot, 'nonprotocol.log'), line + '\n'); }
});
child.on('exit', code => {
  for (const request of pending.values()) request.reject(new Error(`REA exited ${code}`));
  pending.clear();
  closed = true;
});
function rpc(method, params) {
  const id = ++sequence;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
function decoded(result) {
  if (result.structuredContent) return result.structuredContent;
  for (const content of result.content ?? []) {
    if (content.type === 'text') { try { return JSON.parse(content.text); } catch {} }
  }
  return result;
}
async function call(request, filename) {
  if (!allowed.has(request.name)) throw new Error(`Unapproved analysis tool: ${request.name}`);
  const started = new Date().toISOString();
  const result = await rpc('tools/call', { name: request.name, arguments: request.arguments ?? {} });
  const value = decoded(result);
  const record = { request, started, finished: new Date().toISOString(), isError: result.isError === true, raw_response: result, response: value };
  const recordBytes = JSON.stringify(record, null, 2) + '\n';
  await writeFile(join(output, 'evidence', filename), recordBytes, { flag: 'wx' });
  await appendFile(join(output, 'requests.jsonl'), JSON.stringify({ filename, request, sha256: createHash('sha256').update(recordBytes).digest('hex'), request_sha256: createHash('sha256').update(JSON.stringify(request)).digest('hex'), raw_response_sha256: createHash('sha256').update(JSON.stringify(result)).digest('hex') }) + '\n');
  const evidence = value.evidence_id ?? value.result?.evidence_id ?? null;
  console.log(JSON.stringify({ filename, tool: request.name, isError: record.isError, evidence_id: evidence, result: request.name === 'open_binary' || request.name === 'close_binary' || result.isError ? value : undefined }));
  if (record.isError) {
    // The same provider blocker can recur under a different operation name.
    const fingerprint = value.error?.code ?? value.code ?? JSON.stringify(value);
    const count = (failures.get(fingerprint) ?? 0) + 1;
    failures.set(fingerprint, count);
    if (count >= 2) throw new Error('Repeated identical tool failure; stopping as instructed.');
  }
  return value;
}
try {
  await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'sunny-native-deep', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const catalog = await rpc('tools/list', {});
  await writeFile(join(privateRoot, 'tool-catalog.json'), JSON.stringify(catalog, null, 2));
  console.log(`READY: write numbered JSON requests {name,arguments} into ${join(privateRoot, 'inbox')}; close_binary ends this process.`);
  while (!closed) {
    const files = (await readdir(join(privateRoot, 'inbox'))).filter(name => /^\d+.*\.json$/.test(name)).sort();
    for (const filename of files) {
      if ((await readdir(join(privateRoot, 'done'))).includes(filename)) continue;
      let requestBytes;
      try { requestBytes = await readFile(join(privateRoot, 'inbox', filename), 'utf8'); }
      catch (error) { if (error.code === 'ENOENT') continue; throw error; }
      const request = JSON.parse(requestBytes);
      await call(request, filename);
      await writeFile(join(privateRoot, 'done', filename), '', { flag: 'wx' });
      if (request.name === 'close_binary') { closed = true; break; }
    }
    if (!closed) await new Promise(resolve => setTimeout(resolve, 500));
  }
} finally {
  if (!closed) {
    try { await call({ name: 'close_binary', arguments: {} }, '999-emergency-close.json'); } catch (error) { console.error(String(error)); }
  }
  child.stdin.end();
}
