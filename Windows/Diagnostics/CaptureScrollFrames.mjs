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
        if (line !== 'stop') throw new Error('Unknown frame capture command.');
        break;
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
