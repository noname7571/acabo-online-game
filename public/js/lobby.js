document.addEventListener('DOMContentLoaded', () => {
    connectWebSocket(handleWSMessage);
    renderLobbyForm();
});

let currentLobbyId = null;
let currentPlayerId = null;

function renderLobbyForm() {
    document.getElementById('lobby-section').innerHTML =
        '<input type="text" id="playerName" placeholder="Enter your name">' +
        '<button id="createLobbyBtn">Create Lobby</button>' +
        '<input type="text" id="lobbyId" placeholder="Lobby ID">' +
        '<button id="joinLobbyBtn">Join Lobby</button>';

    document.getElementById('createLobbyBtn').onclick = () => {
        const playerName = document.getElementById('playerName').value;
        sendWSMessage({ type: 'create_lobby', playerName });
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
            '<button id="startGameBtn">Start Game</button>' +
            '<button id="leaveLobbyBtn">Back</button>';

        document.getElementById('startGameBtn').onclick = () => {
            sendWSMessage({ type: 'start_game', lobbyId: currentLobbyId });
        };
        document.getElementById('leaveLobbyBtn').onclick = () => {
            // re-render the create/join form so user can change name or create a new lobby
            renderLobbyForm();
        };
    } else if (msg.type === 'game_start') {
        if (msg.selfPlayerId) { currentPlayerId = msg.selfPlayerId; window.currentPlayerId = msg.selfPlayerId; }
        document.getElementById('lobby-section').style.display = 'none';
        // reset pending draw
        window._pendingDraw = null;
        showGame(msg.gameState);
    } else if (msg.type === 'game_update') {
        if (msg.selfPlayerId) { currentPlayerId = msg.selfPlayerId; window.currentPlayerId = msg.selfPlayerId; }
        showGame(msg.gameState, msg.result);
    } else if (msg.type === 'peek_result') {
        // temporarily reveal local card
        revealTemp(window.currentPlayerId, msg.index);
        const log = document.getElementById('game-log');
        const text = `Peeked card [${msg.index}]: ${msg.card}`;
        if (log) {
            const el = document.createElement('div'); el.className = 'game-msg'; el.textContent = text; log.appendChild(el);
            setTimeout(() => el.remove(), 6000);
        } else console.log(text);
    } else if (msg.type === 'ability_peek') {
        // reveal target card for me only
        revealTemp(msg.targetPlayer, msg.index);
        const log = document.getElementById('game-log');
        const text = `Ability peek: player ${msg.targetPlayer} card[${msg.index}] = ${msg.card}`;
        if (log) {
            const el = document.createElement('div'); el.className = 'game-msg'; el.textContent = text; log.appendChild(el);
            setTimeout(() => el.remove(), 6000);
        } else console.log(text);
    } else if (msg.type === 'ability_spy') {
        revealTemp(msg.targetPlayer, msg.index);
        const log = document.getElementById('game-log');
        const text = `Ability spy: player ${msg.targetPlayer} card[${msg.index}] = ${msg.card}`;
        if (log) {
            const el = document.createElement('div'); el.className = 'game-msg'; el.textContent = text; log.appendChild(el);
            setTimeout(() => el.remove(), 6000);
        } else console.log(text);
    } else if (msg.type === 'draw_offer') {
        // store pending draw for the local player and re-render
        window._pendingDraw = msg.card;
        showGame(msg.gameState || (window._lastGameState || {}));
    } else if (msg.type === 'error') {
        const log = document.getElementById('game-log');
        const text = `Error: ${msg.message}`;
        if (log) {
            const el = document.createElement('div'); el.className = 'game-error'; el.textContent = text; log.appendChild(el);
        } else {
            alert(msg.message);
        }
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
