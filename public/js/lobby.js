document.addEventListener('DOMContentLoaded', () => {
    connectWebSocket(handleWSMessage);
    renderLobbyForm();
});

let currentLobbyId = null;
let currentPlayerId = null;

function renderLobbyForm() {
    document.getElementById('lobby-section').innerHTML =
        '<input type="text" id="playerName" placeholder="Enter your name">' +
        '<div><label><input type="checkbox" id="timerEnabled" checked> Enable timer</label>' +
        ' <label>Seconds: <input type="number" id="turnSeconds" value="15" min="1" max="60" style="width:50px"></label></div>' +
        '<button id="createLobbyBtn">Create Lobby</button>' +
        '<input type="text" id="lobbyId" placeholder="Lobby ID">' +
        '<button id="joinLobbyBtn">Join Lobby</button>';

    document.getElementById('createLobbyBtn').onclick = () => {
        const playerName = document.getElementById('playerName').value;
        const timerEnabled = document.getElementById('timerEnabled').checked;
        const turnSeconds = parseInt(document.getElementById('turnSeconds').value) || 15;
        sendWSMessage({ type: 'create_lobby', playerName, timerEnabled, turnSeconds });
    };
    document.getElementById('joinLobbyBtn').onclick = () => {
        const playerName = document.getElementById('playerName').value;
        const lobbyId = document.getElementById('lobbyId').value;
        sendWSMessage({ type: 'join_lobby', lobbyId, playerName });
    };
}

function handleWSMessage(msg) {
    console.log('WS message', msg);
    if (msg.type === 'lobby_update') {
        if (msg.selfPlayerId) { currentPlayerId = msg.selfPlayerId; window.currentPlayerId = msg.selfPlayerId; }
        currentLobbyId = msg.lobbyId;
        document.getElementById('lobby-section').innerHTML =
            `<h2>Lobby: ${msg.lobbyId}</h2>` +
            `<div>Players: ${msg.players ? msg.players.length : 0}</div>` +
            `<div><label><input type="checkbox" id="timerEnabled" ${msg.timerEnabled ? 'checked' : ''}> Enable timer</label>` +
            ` <label>Seconds: <input type="number" id="turnSeconds" value="${msg.turnSeconds||15}" min="1" max="60" style="width:50px"></label></div>` +
            '<button id="startGameBtn">Start Game</button>' +
            '<button id="leaveLobbyBtn">Back</button>';

        document.getElementById('startGameBtn').onclick = () => {
            sendWSMessage({ type: 'start_game', lobbyId: currentLobbyId });
        };
        // when timer inputs change, broadcast new settings
        const timerChk = document.getElementById('timerEnabled');
        const timerInput = document.getElementById('turnSeconds');
        function sendSettings(){
            sendWSMessage({ type:'update_settings', lobbyId: currentLobbyId, timerEnabled: timerChk.checked, turnSeconds: parseInt(timerInput.value)||15 });
        }
        timerChk.onchange = sendSettings;
        timerInput.onchange = sendSettings;
        document.getElementById('leaveLobbyBtn').onclick = () => {
            // re-render the create/join form so user can change name or create a new lobby
            renderLobbyForm();
        };
    } else if (msg.type === 'game_start') {
        if (msg.selfPlayerId) { currentPlayerId = msg.selfPlayerId; window.currentPlayerId = msg.selfPlayerId; }
        // store timer config
        window._timerEnabled = msg.timerEnabled;
        window._turnSeconds = msg.turnSeconds;
        document.getElementById('lobby-section').style.display = 'none';
        // reset pending draw
        window._pendingDraw = null;
        showGame(msg.gameState);
    } else if (msg.type === 'game_update') {
        if (msg.selfPlayerId) { currentPlayerId = msg.selfPlayerId; window.currentPlayerId = msg.selfPlayerId; }
        window._timerEnabled = msg.timerEnabled;
        window._turnSeconds = msg.turnSeconds;
        showGame(msg.gameState, msg.result);
    } else if (msg.type === 'peek_result') {
        // temporarily reveal local card (initial peek) without logging text
        revealTemp(window.currentPlayerId, msg.index);
    } else if (msg.type === 'ability_peek') {
        // reveal target card for me only
        revealTemp(msg.targetPlayer, msg.index);
        // inform in the info overlay
        if (typeof showLog === 'function') showLog(`Peeked player ${msg.targetPlayer} card`);
    } else if (msg.type === 'ability_spy') {
        revealTemp(msg.targetPlayer, msg.index);
        if (typeof showLog === 'function') showLog(`Spied player ${msg.targetPlayer} card`);
    } else if (msg.type === 'error') {
        // show error messages in overlay
        if (typeof showLog === 'function') showLog(msg.message);
        else {
            const log = document.getElementById('game-log'); if (log) log.textContent = msg.message;
        }
    } else if (msg.type === 'draw_offer') {
        // store pending draw for the local player and re-render
        window._pendingDraw = msg.card;
        showGame(msg.gameState || (window._lastGameState || {}));
    }
}

// helper to flash temporary reveal
function revealTemp(playerId, idx) {
    window._tempReveals = window._tempReveals || {};
    if (!window._tempReveals[playerId]) window._tempReveals[playerId] = {};
    window._tempReveals[playerId][idx] = true;
    // remove after 2 seconds
    setTimeout(() => {
        if (window._tempReveals && window._tempReveals[playerId]) {
            delete window._tempReveals[playerId][idx];
        }
        showGame(window._lastGameState || {});
    }, 2000);
}
