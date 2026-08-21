import assert from "node:assert/strict";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { MiRecordStream, isWriteError, miArray, miString, miTuple, parseMiRecord } = require("../out/gdbMi.js");

test("GDB MI transport accepts Node null and undefined successful write callbacks", () => {
  assert.equal(isWriteError(null), false);
  assert.equal(isWriteError(undefined), false);
  assert.equal(isWriteError(new Error("write failed")), true);
});

test("GDB MI parser handles tokened results, tuples, lists, and repeated fields", () => {
  const record = parseMiRecord('17^done,bkpt={number="2",line="41"},threads=[{id="1",name="main"},{id="2",name="worker"}],value="first",value="second"');
  assert.equal(record.token, 17);
  assert.equal(record.kind, "^");
  assert.equal(record.name, "done");
  assert.equal(miString(miTuple(record.results.bkpt).line), "41");
  assert.deepEqual(miArray(record.results.threads).map(value => miString(miTuple(value).id)), ["1", "2"]);
  assert.deepEqual(miArray(record.results.value), ["first", "second"]);
});

test("GDB MI parser decodes streams, escapes, async stops, and incomplete lines", () => {
  assert.equal(parseMiRecord('~"hello\\nworld\\t\\042"').text, 'hello\nworld\t"');
  const stopped = parseMiRecord('*stopped,reason="breakpoint-hit",thread-id="3",frame={func="ct_m_test",line="9"}');
  assert.equal(stopped.kind, "*");
  assert.equal(miString(stopped.results.reason), "breakpoint-hit");
  assert.equal(miString(miTuple(stopped.results.frame).func), "ct_m_test");
  assert.equal(parseMiRecord('(gdb)'), undefined);
  assert.equal(parseMiRecord('ordinary console output'), undefined);
});

test("GDB MI stream parser preserves fragmented records", () => {
  const stream = new MiRecordStream();
  assert.deepEqual(stream.push('4^do'), []);
  assert.deepEqual(stream.push('ne,value="42"\r'), []);
  const records = stream.push('\n*running,thread-id="all"\n');
  assert.equal(records.length, 2);
  assert.equal(records[0].token, 4);
  assert.equal(miString(records[0].results.value), "42");
  assert.equal(records[1].name, "running");
});
