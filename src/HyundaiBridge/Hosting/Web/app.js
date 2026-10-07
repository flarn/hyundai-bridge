const text = (id, value) => { document.getElementById(id).textContent = value; };
const dateFormat = new Intl.DateTimeFormat('sv-SE', { month:'short', day:'numeric', hour:'2-digit', minute:'2-digit', second:'2-digit' });
const date = value => value ? dateFormat.format(new Date(value)) : '—';
const number = value => value == null ? '—' : new Intl.NumberFormat('sv-SE').format(value);
const operations = { discovery:'Hämta fordonslista', refresh:'Förnya session', certificate:'Hämta inloggningscertifikat', signin:'Logga in', exchange:'Skapa session', authorize:'Auktorisera inloggning' };
const outcomes = { network:'Nätfel', timeout:'Timeout', cancelled:'Avbrutet' };
const pill = (id, label, state) => { const el = document.getElementById(id); el.textContent = label; el.className = 'pill' + (state == null ? '' : state ? ' ok' : ' error'); };
function render(data) {
  text('api-title', data.apiReachable == null ? 'Inväntar första hämtningen' : data.apiReachable ? 'Kontakt fungerar' : 'Senaste hämtningen misslyckades');
  text('api-description', data.apiReachable == null ? 'Status visas när bryggan har kontaktat Hyundai.' : data.apiReachable ? 'Senaste Hyundai-hämtningen lyckades.' : 'Tidigare uppgifter behålls. Bryggan väntar före nästa försök.');
  pill('api-pill', data.apiReachable == null ? 'Inväntar hämtning' : data.apiReachable ? 'OK' : 'Kontaktfel', data.apiReachable);
  pill('mqtt-pill', data.mqttConnected ? 'Ansluten' : 'Frånkopplad', data.mqttConnected);
  text('mqtt-title', data.mqttConnected ? 'Transport ansluten' : 'Transport frånkopplad');
  text('last-success', date(data.lastSuccessAt));
  text('next-poll', data.nextPollAt ? date(data.nextPollAt) : data.lastSuccessAt ? 'Ingen planerad' : data.lastFailureAt ? 'Stoppad · starta om bryggan' : 'Inväntar uppstart');
  text('request-count', number(data.requestCount)); text('failure-count', number(data.requestFailures));
  text('rate-limit-count', number(data.rateLimitedResponses));
  text('average-duration', data.averageDurationMs == null ? '—' : number(Math.round(data.averageDurationMs)) + ' ms');
  text('session-source', ({stored:'Sparad session', login:'Inloggning', refresh:'Förnyelse'})[data.sessionSource] ?? 'Ingen session ännu');
  text('session-expiry', date(data.sessionExpiresAt)); text('session-counts', number(data.logins) + ' / ' + number(data.renewals));
  text('vehicle-count', number(data.vehicleCount)); text('last-failure', date(data.lastFailureAt));
  text('failure-type', ({HttpRequestException:'Nätverksfel', OperationCanceledException:'Timeout', HyundaiException:'API- eller svarsfel', IOException:'Lagringsfel'})[data.lastFailure] ?? data.lastFailure ?? 'Inget registrerat fel');
  text('started-at', 'Statistik sedan ' + date(data.startedAt));
  const rows = document.getElementById('request-rows'); rows.replaceChildren();
  for (const request of data.recentRequests) {
    const row = document.createElement('tr');
    [date(request.at), operations[request.operation] ?? request.operation, request.statusCode ?? outcomes[request.failure] ?? 'Okänt', number(Math.round(request.durationMs)) + ' ms'].forEach((value, index) => {
      const cell = document.createElement('td'); cell.textContent = value;
      if (index === 2) cell.className = 'result' + (request.statusCode >= 400 || request.failure ? ' error' : '');
      row.append(cell);
    }); rows.append(row);
  }
  if (!data.recentRequests.length) { const row = document.createElement('tr'); const cell = document.createElement('td'); cell.colSpan = 4; cell.className = 'empty'; cell.textContent = 'Inga registrerade anrop ännu.'; row.append(cell); rows.append(row); }
  text('live-status', 'Uppdaterad ' + date(data.observedAt));
  document.getElementById('live-status').classList.remove('error');
  document.getElementById('connection-error').hidden = true;
}
async function update() {
  try {
    const response = await fetch('/api/status', { cache:'no-store', signal:AbortSignal.timeout(4000) });
    if (!response.ok) throw new Error('Status unavailable');
    render(await response.json());
  } catch {
    text('live-status', 'Ingen kontakt med bryggan');
    document.getElementById('live-status').classList.add('error');
    document.getElementById('connection-error').hidden = false;
  } finally { setTimeout(update, 5000); }
}
update();
