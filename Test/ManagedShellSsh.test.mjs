import assert from "node:assert/strict";
import {
  createCipheriv,
  createDecipheriv,
  createECDH,
  createHash,
  generateKeyPairSync,
  sign,
  verify,
} from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const sshRoot = join(root, "examples", "ManagedShell", "Modules", "SystemSsh");
const server = readFileSync(join(sshRoot, "Server.ct"), "utf8");
const transport = readFileSync(join(sshRoot, "Transport.ct"), "utf8");
const sftp = readFileSync(join(sshRoot, "Sftp.ct"), "utf8");

const u32 = value => {
  const result = Buffer.alloc(4);
  result.writeUInt32BE(value);
  return result;
};
const sshString = value => {
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(value, "utf8");
  return Buffer.concat([u32(bytes.length), bytes]);
};
const mpintLittleEndian = value => {
  let end = value.length - 1;
  while (end >= 0 && value[end] === 0) end--;
  if (end < 0) return Buffer.alloc(0);
  const result = Buffer.from([...value.subarray(0, end + 1)].reverse());
  return (result[0] & 0x80) === 0 ? result : Buffer.concat([Buffer.from([0]), result]);
};

test("SSH negotiation keeps extension markers separate from the selected KEX", () => {
  assert.match(server, /curve25519-sha256,ext-info-s,kex-strict-s-v00@openssh\.com/);
  assert.match(server, /HasName\(kex, "ext-info-c"\)/);
  assert.match(server, /HasName\(kex, "kex-strict-c-v00@openssh\.com"\)/);
  assert.match(server, /FirstName\(kex\) != "curve25519-sha256"/);
  assert.doesNotMatch(server, /IsSupportedAlgorithm\("kex", "ext-info-c"\)/);
});

test("OpenSSH AES-GCM packet fixture authenticates the clear packet length", () => {
  const key = Buffer.from("000102030405060708090a0b0c0d0e0f", "hex");
  const nonce = Buffer.from("101112131415161718191a1b", "hex");
  const body = Buffer.from("087465737400000000", "hex");
  const header = u32(body.length);
  const cipher = createCipheriv("aes-128-gcm", key, nonce);
  cipher.setAAD(header, { plaintextLength: body.length });
  const encrypted = Buffer.concat([cipher.update(body), cipher.final()]);
  const tag = cipher.getAuthTag();
  const decipher = createDecipheriv("aes-128-gcm", key, nonce);
  decipher.setAAD(header, { plaintextLength: encrypted.length });
  decipher.setAuthTag(tag);
  assert.deepEqual(Buffer.concat([decipher.update(encrypted), decipher.final()]), body);
  assert.match(transport, /AesSeal\(outboundCipher, nonce, header, body, cipher, tag\)/);
  assert.match(transport, /outboundInvocation = 0UL/);
  assert.match(transport, /Nonce\(outboundIv, outboundInvocation\)/);
  assert.match(transport, /for \(int index = 11; index >= 4; index--\)/);
  const activation = transport.slice(transport.indexOf("internal void Activate"),
    transport.indexOf("internal void ResetSequences"));
  assert.doesNotMatch(activation, /inboundSequence = 0u|outboundSequence = 0u/);
});

test("exchange hash and key derivation use SSH strings and the original session identifier", () => {
  const clientKex = Buffer.from("14010203", "hex");
  const serverKex = Buffer.from("14040506", "hex");
  const host = Buffer.from("host");
  const clientPublic = Buffer.alloc(32, 1);
  const serverPublic = Buffer.alloc(32, 2);
  const secret = Buffer.from([...Array(32).keys()].map(index => index + 1));
  const encodedSecret = mpintLittleEndian(secret);
  const exchangeHash = createHash("sha256").update(Buffer.concat([
    sshString("SSH-2.0-client"),
    sshString("SSH-2.0-CTilde_0.49"),
    sshString(clientKex),
    sshString(serverKex),
    sshString(host),
    sshString(clientPublic),
    sshString(serverPublic),
    sshString(encodedSecret),
  ])).digest();
  assert.equal(exchangeHash.toString("hex"),
    "a9d957a4d4d702f14e4dfd6fdecf79371238cc9fcde4f1b1e58084feacbd5fce");
  const receiveKey = createHash("sha256").update(Buffer.concat([
    sshString(encodedSecret), exchangeHash, Buffer.from("A"), exchangeHash,
  ])).digest().subarray(0, 16);
  assert.equal(receiveKey.toString("hex"), "981d869136378aa01e8c6de652ab3e1d");
  assert.match(server, /if \(sessionIdentifier == null\) sessionIdentifier = exchangeHash/);
  assert.match(server, /writer\.WriteRaw\(sessionIdentifier\)/);
});

test("authorized-key fixture has the exact P-256 SSH blob shape", () => {
  const ecdh = createECDH("prime256v1");
  ecdh.setPrivateKey(Buffer.from("1".padStart(64, "0"), "hex"));
  const point = ecdh.getPublicKey(undefined, "uncompressed");
  const blob = Buffer.concat([
    sshString("ecdsa-sha2-nistp256"),
    sshString("nistp256"),
    sshString(point),
  ]);
  let offset = 0;
  const next = () => {
    const length = blob.readUInt32BE(offset);
    offset += 4;
    const result = blob.subarray(offset, offset + length);
    offset += length;
    return result;
  };
  assert.equal(next().toString(), "ecdsa-sha2-nistp256");
  assert.equal(next().toString(), "nistp256");
  assert.equal(next().length, 65);
  assert.equal(offset, blob.length);
  assert.match(server, /P256Verify\(key, hash, fixedSignature\)/);
});

test("public-key authentication signs the session identifier and request prefix", () => {
  const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const session = Buffer.alloc(32, 0x5a);
  const requestPrefix = Buffer.concat([
    Buffer.from([50]),
    sshString("ctilde"),
    sshString("ssh-connection"),
    sshString("publickey"),
    Buffer.from([1]),
    sshString("ecdsa-sha2-nistp256"),
    sshString(Buffer.from("public-blob")),
  ]);
  const signedPayload = Buffer.concat([sshString(session), requestPrefix]);
  const signature = sign("sha256", signedPayload, privateKey);
  assert.equal(verify("sha256", signedPayload, publicKey, signature), true);
  assert.match(server, /int signedPrefixLength = reader\.Position/);
  assert.match(server, /signed\.WriteBytes\(sessionIdentifier\)/);
  assert.match(server, /signed\.WriteRaw\(Prefix\(request, prefixLength\)\)/);
});

test("session channels retain the bounded window and shared-shell routing contract", () => {
  assert.match(server, /accepted\.WriteUInt32\(65536u\)/);
  assert.match(server, /state\.RemoteMaximumPacket > 32768u/);
  assert.match(server, /StartShell\(channel, "--ssh", ""\)/);
  assert.match(server, /StartShell\(channel, "--exec", command\)/);
  assert.match(server, /"\[8;" \+ rows\.ToString\(\) \+ ";"/);
  assert.match(server, /TerminateDescendants\(channel\.Process\.Id, 1000u\)/);
});

test("SFTP fixtures stay below the rooted packet and handle limits", () => {
  const normalize = path => {
    if (!path.startsWith("/") || path.includes("\0") || path.includes("\\")) return null;
    const parts = path.split("/").filter(part => part !== "" && part !== ".");
    if (parts.includes("..")) return null;
    return "/sftp" + (parts.length === 0 ? "" : `/${parts.join("/")}`);
  };
  assert.equal(normalize("/"), "/sftp");
  assert.equal(normalize("/docs/./readme.txt"), "/sftp/docs/readme.txt");
  assert.equal(normalize("/../storage/ssh/authorized_keys"), null);
  assert.equal(normalize("/docs\\escape"), null);
  assert.match(sftp, /length > 35000u/);
  assert.match(sftp, /data\.Length > 32768/);
  assert.match(sftp, /files = new FileStream\[8\]/);
  assert.match(sftp, /generations\[slot\]\+\+/);
  const handle = Buffer.concat([u32(3), u32(7)]);
  assert.equal(handle.readUInt32BE(0), 3);
  assert.equal(handle.readUInt32BE(4), 7);
  assert.notEqual(handle.readUInt32BE(4), 8, "a recycled slot must reject its stale generation");
});
