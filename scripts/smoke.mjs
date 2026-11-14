import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';

const base = process.env.FLEETPROOF_TEST_API ?? 'http://127.0.0.1:5080/api/v1';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a disposable local database');

async function request(path, { method = 'GET', token, body, status = 200 } = {}) {
  const response = await fetch(base + path, {
    method,
    headers: {
      ...(body ? { 'Content-Type': 'application/json' } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(5000),
  });
  assert.equal(response.status, status, `${method} ${path}`);
  return response.headers.get('content-type')?.includes('application/json') ? response.json() : null;
}

const suffix = randomBytes(5).toString('hex');
const password = randomBytes(18).toString('hex');
const first = { username: `smoke_a_${suffix}`, password };
const second = { username: `smoke_b_${suffix}`, password };
await request('/health');
await request('/vehicles', { status: 401 });
await request('/authentication/sign-up', { method: 'POST', body: first, status: 201 });
await request('/authentication/sign-up', { method: 'POST', body: { ...first, username: first.username.toUpperCase() }, status: 409 });
await request('/authentication/sign-up', { method: 'POST', body: second, status: 201 });
await request('/authentication/sign-in', { method: 'POST', body: { ...first, password: 'wrong-password' }, status: 401 });
const a = await request('/authentication/sign-in', { method: 'POST', body: first });
const b = await request('/authentication/sign-in', { method: 'POST', body: second });
assert.ok(a.token && b.token);
assert.equal(a.username, first.username);
assert.equal((await request('/account', { token: a.token })).id, a.id);
const vehicle = { plate: 'ABC-123', type: 'Auto', site: 'Lima' };
const created = await request('/vehicles', { method: 'POST', token: a.token, body: { ...vehicle, userId: b.id }, status: 201 });
await request('/vehicles', { method: 'POST', token: a.token, body: vehicle, status: 409 });
assert.deepEqual(await request('/vehicles', { token: b.token }), []);
await request(`/vehicles/${created.id}`, { token: b.token, status: 404 });
await request(`/vehicles/${created.id}`, { method: 'PUT', token: b.token, body: vehicle, status: 404 });
await request(`/vehicles/${created.id}`, { method: 'PUT', token: a.token, body: { ...vehicle, site: 'Callao' } });
assert.equal((await request('/vehicles', { token: a.token }))[0].site, 'Callao');
await request('/fleets', { status: 401 });
await request('/assignments', { status: 401 });
await request('/fleets', { method: 'POST', token: a.token, body: { name: 'Sin plan' }, status: 403 });
const active = { service: 'fleet', plan: 'weekly', status: 'active', paymentStatus: 'completed' };
await request('/subscriptions', { method: 'POST', token: a.token, body: { ...active, paymentStatus: 'pending' }, status: 400 });
const plan = await request('/subscriptions', { method: 'POST', token: a.token, body: active, status: 201 });
assert.deepEqual(await request('/subscriptions', { token: b.token }), []);
await request(`/subscriptions/${plan.id}`, { method: 'PUT', token: b.token, body: active, status: 404 });
await request(`/subscriptions/${plan.id}`, { method: 'PUT', token: a.token, body: { ...active, plan: 'monthly' } });
await request('/subscriptions', { method: 'POST', token: b.token, body: active, status: 201 });
await request('/fleets', { method: 'POST', token: a.token, body: { name: ' ' }, status: 400 });
const fleet = await request('/fleets', { method: 'POST', token: a.token, body: { name: ' Lima Norte ', userId: b.id }, status: 201 });
const otherFleet = await request('/fleets', { method: 'POST', token: b.token, body: { name: 'Callao' }, status: 201 });
assert.deepEqual(fleet, { id: fleet.id, name: 'Lima Norte' });
assert.deepEqual(await request('/fleets', { token: a.token }), [fleet]);
assert.deepEqual(await request('/fleets', { token: b.token }), [otherFleet]);

const assignment = { fleetId: fleet.id, vehicleId: created.id, responsible: ' Responsable de prueba ' };
await request('/assignments', { method: 'POST', token: a.token, body: { ...assignment, responsible: '' }, status: 400 });
await request('/assignments', { method: 'POST', token: a.token, body: { ...assignment, fleetId: otherFleet.id }, status: 404 });
await request('/assignments', { method: 'POST', token: b.token, body: { ...assignment, fleetId: otherFleet.id }, status: 404 });
const saved = await request('/assignments', { method: 'POST', token: a.token, body: { ...assignment, userId: b.id }, status: 201 });
assert.deepEqual(saved, { id: saved.id, ...assignment, responsible: 'Responsable de prueba' });
assert.deepEqual(await request('/assignments', { token: a.token }), [saved]);
assert.deepEqual(await request('/assignments', { token: b.token }), []);
const nextFleet = await request('/fleets', { method: 'POST', token: a.token, body: { name: 'Sur' }, status: 201 });
await request('/assignments', { method: 'POST', token: a.token, body: { ...assignment, fleetId: nextFleet.id }, status: 409 });
await request(`/subscriptions/${plan.id}`, { method: 'PUT', token: a.token, body: { ...active, status: 'cancelled' } });
await request('/fleets', { method: 'POST', token: a.token, body: { name: 'Plan cancelado' }, status: 403 });
await request('/assignments', { method: 'POST', token: a.token, body: assignment, status: 403 });
assert.deepEqual(await request('/assignments', { token: a.token }), [saved]);
await request(`/subscriptions/${plan.id}`, { method: 'PUT', token: a.token, body: active });
const reportBody = { plate: created.plate, status: 'published', serviceType: 'individual',
  paymentStatus: 'completed', findings: [] };
const report = await request('/reports', { method: 'POST', token: a.token, body: reportBody, status: 201 });
assert.equal(report.findings.length, 3);
assert.ok(report.findings.every(f => f.simulated));
assert.deepEqual(await request('/reports', { token: b.token }), []);
await request(`/reports/${report.id}`, { method: 'PUT', token: b.token, body: report, status: 404 });
await request('/reports', { method: 'POST', token: b.token, status: 404,
  body: { ...reportBody, vehicleId: created.id, serviceType: 'fleet', paymentStatus: 'none', status: 'draft' } });
const caseBody = { reportId: report.id, plate: report.plate, title: 'Caso de prueba', severity: 'medium', status: 'open' };
await request('/cases', { method: 'POST', token: b.token, body: caseBody, status: 404 });
const resolution = await request('/cases', { method: 'POST', token: a.token, body: caseBody, status: 201 });
await request(`/cases/${resolution.id}`, { method: 'PUT', token: a.token, body: { ...caseBody, status: 'resolved' }, status: 400 });
await request(`/cases/${resolution.id}`, { method: 'PUT', token: a.token,
  body: { ...caseBody, status: 'resolved', evidence: 'https://example.com/evidence', note: 'Validado' } });
const watchBody = { vehicleId: created.id, status: 'active', intervalMinutes: 60 };
await request('/monitorings', { method: 'POST', token: b.token, body: watchBody, status: 404 });
const watch = await request('/monitorings', { method: 'POST', token: a.token, body: watchBody, status: 201 });
await request('/monitorings', { method: 'POST', token: a.token, body: watchBody, status: 409 });
const historyPath = `/monitorings/${watch.id}/checks`;
const readHistory = () => request(historyPath, { token: a.token });
const instantOf = check => Date.parse(check.checkedAt);
assert.deepEqual(await readHistory(), []);
const alertBody = { monitoringId: watch.id, vehicleId: created.id, changes: [] };
await request('/alerts', { method: 'POST', token: a.token, body: alertBody, status: 409 });
const check = at => request(`/monitorings/${watch.id}`, { method: 'PUT', token: a.token,
  body: { ...watch, lastCheckedAt: at instanceof Date ? at.toISOString() : at } });
const firstCheckAt = new Date(Date.now() - 60_000);
await check(firstCheckAt);
let history = await readHistory();
assert.equal(history.length, 1);
assert.equal(history[0].result, 'no_changes');
assert.deepEqual(Object.keys(history[0]).sort(), ['checkedAt', 'id', 'result']);
assert.equal(instantOf(history[0]), firstCheckAt.getTime());
await request(`/vehicles/${created.id}`, { method: 'PUT', token: a.token, body: { ...vehicle, risk: 'high' } });
const alert = await request('/alerts', { method: 'POST', token: a.token, body: alertBody, status: 201 });
assert.ok(alert.changes.some(change => change.field === 'risk' && change.after === 'high'));
assert.deepEqual(await request('/alerts', { token: b.token }), []);
const secondCheckAt = new Date();
const updatedWatch = await check(secondCheckAt);
assert.equal(updatedWatch.snapshot.risk, 'high');
history = await readHistory();
assert.deepEqual(history.map(item => item.result), ['change_detected', 'no_changes']);
assert.equal(instantOf(history[0]), secondCheckAt.getTime());
assert.deepEqual((await request('/alerts', { token: a.token })).map(item => item.id), [alert.id]);

// Retries and late checks do not add rows or replace the latest state.
const sameInstantOtherFormat = secondCheckAt.toISOString().replace('Z', '+00:00');
for (const repeated of [secondCheckAt, sameInstantOtherFormat, firstCheckAt]) {
  const response = await check(repeated);
  assert.equal(response.lastCheckedAt, updatedWatch.lastCheckedAt);
  assert.equal((await readHistory()).length, 2);
}
await request(`/monitorings/${watch.id}`, { method: 'PUT', token: a.token, status: 409,
  body: { ...watch, lastCheckedAt: new Date(firstCheckAt.getTime() - 1000).toISOString() } });
assert.equal((await readHistory()).length, 2);
assert.equal((await request('/monitorings', { token: a.token }))[0].lastCheckedAt, updatedWatch.lastCheckedAt);

// Pausing, resuming or reporting an instant while paused is not a check.
const paused = await request(`/monitorings/${watch.id}`, { method: 'PUT', token: a.token,
  body: { ...updatedWatch, status: 'inactive', lastCheckedAt: new Date(secondCheckAt.getTime() + 1000).toISOString() } });
assert.equal(paused.lastCheckedAt, updatedWatch.lastCheckedAt);
await request(`/monitorings/${watch.id}`, { method: 'PUT', token: a.token,
  body: { ...updatedWatch, status: 'active', intervalMinutes: 30 } });
assert.equal((await readHistory()).length, 2);

// Two requests with the same new instant at the same time add one row.
const raceAt = new Date(secondCheckAt.getTime() + 5000);
await Promise.all([check(raceAt), check(raceAt)]);
history = await readHistory();
assert.equal(history.length, 3);
assert.equal(history[0].result, 'no_changes');

// The list is limited to 50 rows, newest first.
for (let step = 1; step <= 55; step++) await check(new Date(raceAt.getTime() + step * 1000));
history = await readHistory();
assert.equal(history.length, 50);
assert.equal(instantOf(history[0]), raceAt.getTime() + 55_000);
assert.ok(history.every((item, index) => index === 0 || instantOf(history[index - 1]) > instantOf(item)));

// Other accounts, missing sessions and unknown ids do not reach the history.
await request(historyPath, { token: b.token, status: 404 });
await request(historyPath, { status: 401 });
await request(`/monitorings/${randomUUID()}/checks`, { token: a.token, status: 404 });
const openApi = await (await fetch(new URL('/openapi/v1.json', base), { signal: AbortSignal.timeout(5000) })).json();
assert.ok(openApi.paths['/api/v1/monitorings/{id}/checks']?.get);
// Reading the history does not need an active plan.
await request(`/subscriptions/${plan.id}`, { method: 'PUT', token: a.token, body: { ...active, status: 'cancelled' } });
assert.equal((await readHistory()).length, 50);
await request(`/monitorings/${watch.id}`, { method: 'PUT', token: a.token, body: { ...updatedWatch, status: 'inactive' }, status: 200 });

const newPassword = randomBytes(18).toString('hex');
await request('/account', { method: 'PATCH', token: a.token, body: { username: first.username, currentPassword: 'wrong-password' }, status: 403 });
const renamed = await request('/account', { method: 'PATCH', token: a.token,
  body: { username: first.username + '_updated', currentPassword: password, password: newPassword } });
assert.equal(renamed.signInRequired, true);
await request('/fleets', { token: a.token, status: 401 });
const renewed = await request('/authentication/sign-in', { method: 'POST', body: { username: renamed.username, password: newPassword } });
assert.deepEqual(await request('/assignments', { token: renewed.token }), [saved]);
await request('/account', { method: 'DELETE', token: renewed.token, body: { currentPassword: newPassword }, status: 204 });
await request('/account', { token: renewed.token, status: 401 });
assert.deepEqual(await request('/fleets', { token: b.token }), [otherFleet]);
console.log('OK: six contexts, simulated payment, reports, cases, monitoring, ownership and account lifecycle.');
