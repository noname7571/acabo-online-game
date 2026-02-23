
function showGame(gameState, result) {
    const gameSection = document.getElementById('game-section');
    gameSection.style.display = 'block';
    window._lastGameState = gameState || window._lastGameState;
    gameState = gameState || window._lastGameState || {};
    let html = `<h2>Game: ${gameState.lobbyId}</h2>`;
    if (result) html += `<div style="color:green;">${result}</div>`;
    html += '<div><b>Players:</b><ul>';
    gameState.players.forEach((p, idx) => {
        html += `<li>${p.name} (${p.id === currentPlayerId ? 'You' : ''}) Hand: `;
        html += p.hand.map((c, i) => c ? (p.id === currentPlayerId ? c : (p.revealed[i] ? c : '?')) : '_').join(' ');
        html += '</li>';
    });
    html += '</ul></div>';
    html += `<div><b>Deck:</b> ${gameState.deck.length} cards</div>`;
    html += `<div><b>Discard:</b> ${gameState.discardPile.slice(-1)[0] || 'Empty'}</div>`;
    if (!gameState.gameOver) {
        const current = gameState.players && gameState.players[gameState.currentTurn];
        if (current && current.id === currentPlayerId) {
            // initial peek option (allowed before or during game until used)
            if (current.initialPeeksRemaining && current.initialPeeksRemaining > 0) {
                html += '<div>Initial peek available: ';
                for (let i = 0; i < 4; i++) {
                    html += `<button class="initialPeekBtn" data-idx="${i}">Peek ${i}</button> `;
                }
                html += '</div>';
            }
            // display discard-top action
            if (gameState.discardPile && gameState.discardPile.length > 0) {
                html += `<div>Top discard: ${gameState.discardPile.slice(-1)[0]} <button id="takeDiscardBtn">Take & Swap</button></div>`;
            }
            if (gameState.phase === 'draw') {
                html += '<button id="drawBtn">Draw from deck</button>';
            }
            // if pending draw offer exists for this client, show resolution options
            if (window._pendingDraw) {
                const card = window._pendingDraw;
                html += `<div>You drew: <b>${card}</b></div>`;
                html += '<div>Resolve: ';
                html += '<button id="discardDrawBtn">Discard</button> ';
                html += '<span> or swap with: ';
                for (let i = 0; i < 4; i++) {
                    html += `<button class='resolveSwapBtn' data-idx='${i}'>${i}</button> `;
                }
                html += '</span></div>';
                // if ability card, offer use ability (peek/spy/swap)
                const v = parseInt(card, 10);
                if (v >= 7 && v <= 12) {
                    html += '<div>Use ability: ';
                    html += '<select id="abilityTargetPlayer">';
                    (gameState.players || []).forEach(p => {
                        if (p.id !== currentPlayerId) html += `<option value="${p.id}">${p.name}</option>`;
                    });
                    html += '</select> Card index: <select id="abilityTargetIdx">';
                    for (let i = 0; i < 4; i++) html += `<option value="${i}">${i}</option>`;
                    html += '</select> <button id="useAbilityBtn">Use</button>';
                    html += '</div>';
                }
            }
            html += '<button id="caboBtn">Call Cabo</button>';
        } else {
            html += '<div>Waiting for other player...</div>';
        }
    } else {
        html += `<div style="color:blue;">Game Over! Winner: ${gameState.winner}</div>`;
    }
    gameSection.innerHTML = html;
    if (document.getElementById('drawBtn')) {
        document.getElementById('drawBtn').onclick = () => {
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'draw' });
        };
    }
    if (document.getElementById('takeDiscardBtn')) {
        document.getElementById('takeDiscardBtn').onclick = () => {
            // ask which card index to swap with
            const idx = prompt('Swap discard with which card index (0-3)?');
            if (idx !== null) sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'take_discard', payload: { cardIndex: parseInt(idx) } });
        };
    }
    document.querySelectorAll('.initialPeekBtn').forEach(b => {
        b.onclick = () => {
            const idx = parseInt(b.dataset.idx);
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'peek_initial', payload: { index: idx } });
        };
    });
    if (document.getElementById('caboBtn')) {
        document.getElementById('caboBtn').onclick = () => {
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'call_cabo' });
        };
    }
    if (document.getElementById('discardDrawBtn')) {
        document.getElementById('discardDrawBtn').onclick = () => {
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'discard' } });
            window._pendingDraw = null;
        };
    }
    document.querySelectorAll('.resolveSwapBtn').forEach(btn => {
        btn.onclick = () => {
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'swap', cardIndex: parseInt(btn.dataset.idx) } });
            window._pendingDraw = null;
        };
    });
    if (document.getElementById('useAbilityBtn')) {
        document.getElementById('useAbilityBtn').onclick = () => {
            const targetPlayer = document.getElementById('abilityTargetPlayer').value;
            const targetIdx = parseInt(document.getElementById('abilityTargetIdx').value);
            // use the pending draw card as ability (server currently treats abilities via resolve_draw + swap)
            sendWSMessage({ type: 'player_action', lobbyId: gameState.lobbyId, actionType: 'resolve_draw', payload: { action: 'use_ability', targetPlayer, targetIdx } });
            window._pendingDraw = null;
        };
    }
}
