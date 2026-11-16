import { createInterface } from 'node:readline';

const address = new URL(process.argv[2]);
if (address.protocol !== 'ws:' || address.hostname !== '127.0.0.1')
    throw new Error('Frame capture requires the private loopback browser endpoint.');

const socket = new WebSocket(address);
const pending = new Map();
const acknowledgements = [];
const frames = [];
let nextId = 0;
let failure;
let wheelContext;

function command(method, params = {}) {
    const id = ++nextId;
    return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        socket.send(JSON.stringify({ id, method, params }));
    });
}

socket.addEventListener('message', event => {
    try {
        const message = JSON.parse(event.data);
        if (message.id !== undefined) {
            const request = pending.get(message.id);
            if (!request) throw new Error('Unknown browser command response.');
            pending.delete(message.id);
            if (message.error) request.reject(new Error(JSON.stringify(message.error)));
            else request.resolve(message.result);
        } else if (message.method === 'Page.screencastFrame') {
            const { metadata, sessionId } = message.params;
            if (!Number.isFinite(metadata.timestamp) || !Number.isFinite(metadata.scrollOffsetY))
                throw new Error('The browser frame did not include its capture time and scroll position.');
            frames.push({ TimestampMs: metadata.timestamp * 1000, Offset: metadata.scrollOffsetY });
            acknowledgements.push(command('Page.screencastFrameAck', { sessionId })
                .catch(error => { failure = error; }));
        }
    } catch (error) {
        failure = error;
    }
});

socket.addEventListener('close', () => {
    for (const request of pending.values())
        request.reject(new Error('The private browser disconnected during capture.'));
    pending.clear();
});

try {
    await new Promise((resolve, reject) => {
        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', () => reject(new Error('Cannot connect to the private browser.')), { once: true });
    });
    await command('Page.enable');
    await command('Page.startScreencast', { format: 'jpeg', quality: 15, maxWidth: 160, maxHeight: 160, everyNthFrame: 1 });
    process.stdout.write('ready\n');
    const input = createInterface({ input: process.stdin });
    for await (const line of input) {
        if (line === 'stop') break;
        const request = JSON.parse(line);
        if (request.type !== 'wheel' || !Number.isFinite(request.x) || !Number.isFinite(request.y)
            || request.x < 0 || request.x > 1 || request.y < 0 || request.y > 1
            || !Number.isFinite(request.deltaY) || Math.abs(request.deltaY) > 1200)
            throw new Error('Invalid private browser wheel command.');
        if (wheelContext === undefined) {
            const tree = await command('Page.getFrameTree');
            const world = await command('Page.createIsolatedWorld', {
                frameId: tree.frameTree.frame.id, worldName: 'WinNewsWire.IndependentInput'
            });
            wheelContext = world.executionContextId;
        }
        const viewport = await command('Runtime.evaluate', {
            contextId: wheelContext, expression: '({width:innerWidth,height:innerHeight})', returnByValue: true
        });
        const x = viewport.result.value.width * request.x, y = viewport.result.value.height * request.y;
        if (!(x > 0) || !(y > 0)) throw new Error('The private browser viewport is empty.');
        await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        const time = await command('Runtime.evaluate', {
            contextId: wheelContext,
            expression: `(() => {
                const state = globalThis.__wnwPrivateWheelCapture = { events: [] };
                state.listener = event => {
                    if (event.isTrusted && event.deltaY)
                        state.events.push({ created: performance.timeOrigin + event.timeStamp,
                            received: performance.timeOrigin + performance.now() });
                };
                addEventListener('wheel', state.listener, { capture: true, passive: true });
                return performance.timeOrigin + performance.now();
            })()`, returnByValue: true
        });
        const sentAt = time.result.value;
        if (!Number.isFinite(sentAt)) throw new Error('The browser clock is unavailable.');
        await command('Input.synthesizeScrollGesture', {
            x, y, yDistance: -request.deltaY, speed: 120000,
            gestureSourceType: 'mouse', preventFling: true
        });
        const observed = await command('Runtime.evaluate', {
            contextId: wheelContext,
            expression: `(() => {
                const state = globalThis.__wnwPrivateWheelCapture;
                removeEventListener('wheel', state.listener, true);
                delete globalThis.__wnwPrivateWheelCapture;
                return state.events;
            })()`, returnByValue: true
        });
        const delivered = observed.result.value;
        if (!Array.isArray(delivered) || delivered.length === 0)
            throw new Error('No trusted browser wheel event was delivered.');
        process.stdout.write(JSON.stringify({
            Type: 'wheel', RequestedAt: sentAt, FirstAt: delivered[0].created,
            LastAt: delivered.at(-1).created, Events: delivered.length,
            MaximumDispatchDelayMs: Math.max(...delivered.map(event => event.received - event.created))
        }) + '\n');
    }
    input.close();
    await command('Page.stopScreencast');
    await Promise.all(acknowledgements);
    if (failure) throw failure;
    process.stdout.write(JSON.stringify({ ProcessId: process.pid, Frames: frames }) + '\n');
} catch (error) {
    console.error(error);
    process.exitCode = 1;
} finally {
    socket.close();
}
