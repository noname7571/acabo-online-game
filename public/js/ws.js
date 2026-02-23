let ws;
let _wsMessageQueue = [];
let _wsMessageHandler = null;

function connectWebSocket(onMessage) {
    _wsMessageHandler = onMessage;
    ws = new WebSocket('ws://localhost:5000/ws');
    ws.onopen = () => {
        console.log('WebSocket connected');
        // flush queued messages
        while (_wsMessageQueue.length > 0) {
            const m = _wsMessageQueue.shift();
            ws.send(JSON.stringify(m));
        }
    };
    ws.onmessage = (event) => {
        try {
            const data = JSON.parse(event.data);
            if (_wsMessageHandler) _wsMessageHandler(data);
        } catch (e) {
            console.error('Failed to parse WS message', e);
        }
    };
    ws.onclose = () => console.log('WebSocket closed');
    ws.onerror = (err) => console.error('WebSocket error', err);
}

function sendWSMessage(msg) {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(msg));
    } else {
        // queue the message until the socket opens
        _wsMessageQueue.push(msg);
    }
}
