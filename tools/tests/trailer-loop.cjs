// Execute the actual JavaScript embedded in WebTrailer.CreateLoop with a small media/RAF host.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../SeleneCore/src/WebTrailer.cs'), 'utf8');
const loop = source.slice(source.indexOf('public static WebTrailer CreateLoop'));
const start = loop.indexOf('+ "var v=document');
const end = loop.indexOf('var page =', start);
const script = [...loop.slice(start, end).matchAll(/"((?:[^"\\]|\\.)*)"/g)]
  .map(match => JSON.parse('"' + match[1] + '"')).join('').split('</script>')[0];
for (const picture of [true, false]) {
  const events = {}, pending = new Map();
  let next = 0, draws = 0;
  const video = { paused: true, videoWidth: 1920, videoHeight: 1080,
    addEventListener: (name, action) => { events[name] = action; },
    play() { this.paused = false; events.playing?.(); },
    pause() { this.paused = true; events.pause?.(); } };
  const canvas = { getContext: () => ({ drawImage() { draws++; } }) };
  const context = { document: { getElementById: id => id === 'v' ? video : picture ? canvas : null },
    innerWidth: 1920, innerHeight: 1080, addEventListener() {}, setInterval() {},
    requestAnimationFrame: action => { pending.set(++next, action); return next; },
    cancelAnimationFrame: id => pending.delete(id) };
  context.window = context;
  vm.runInNewContext(script, context);
  assert.equal(pending.size, picture ? 1 : 0, 'Only a playing picture schedules canvas frames');
  context.selene.pause();
  assert.equal(pending.size, 0, 'Pausing cancels the canvas loop');
  assert.equal(draws, 0, 'Paused canvas performs no drawing');
  context.selene.play(); events.playing();
  assert.equal(pending.size, picture ? 1 : 0, 'Resume and duplicate playing events create at most one loop');
  if (picture) {
    const [id, action] = pending.entries().next().value; pending.delete(id); action();
    assert.equal(draws, 1); assert.equal(pending.size, 1);
  }
  context.selene.pause();
  assert.equal(pending.size, 0, 'Repeated pause stops resumed work');
}
console.log('Passed trailer canvas pause/resume checks for picture and sound-only playback.');
