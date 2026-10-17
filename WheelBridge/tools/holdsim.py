"""Rough wheel-hold stability sim: rigid wheel (inertia J, R3 3.9 Nm), delayed torque, noisy velocity.
Reports the steady-state oscillation (deg p-p, torque rms) after release from 3 deg off center."""
import math, random

def sim(law, J, lat_ms, T=3.0, dt=0.002):
    random.seed(1)
    n_lat = max(1, round(lat_ms / 1000 / 0.0002))
    h = 0.0002  # physics step
    ang, vel = 3.0, 0.0
    queue = [0.0] * n_lat
    st = {}
    out_hist, ang_hist = [], []
    t, next_ctrl, cmd = 0.0, 0.0, 0.0
    while t < T:
        if t >= next_ctrl:
            meas_vel = vel + random.gauss(0, 1.5)
            meas_ang = round(ang / 0.02) * 0.02
            cmd = law(meas_ang, meas_vel, dt, st)
            next_ctrl += dt
        queue.append(cmd)
        applied = queue.pop(0)
        torque = applied * 3.9 - 0.02 * math.radians(vel)  # small mechanical friction/damping
        acc = math.degrees(torque / J)
        vel += acc * h
        ang += vel * h
        t += h
        if t > T - 1.0:
            ang_hist.append(ang); out_hist.append(applied)
    pp = max(ang_hist) - min(ang_hist)
    rms = math.sqrt(sum(x * x for x in out_hist) / len(out_hist))
    return pp, rms

def old(a, v, dt, st):
    t = (0 - a) / 4 + (0 - v) / 200
    return max(-1, min(1, t))

def new(full=10, damp=120, db=0.2, vel_hz=25, out_hz=40):
    def law(a, v, dt, st):
        kv = 1 - math.exp(-dt * 2 * math.pi * vel_hz)
        st['v'] = st.get('v', 0) + (v - st.get('v', 0)) * kv
        e = -a
        e = math.copysign(max(0, abs(e) - db), e)
        t = math.tanh(e / full) - st['v'] / damp
        t = max(-1, min(1, t))
        ko = 1 - math.exp(-dt * 2 * math.pi * out_hz)
        st['o'] = st.get('o', 0) + (t - st.get('o', 0)) * ko
        return st['o']
    return law

cases = [(J, L) for J in (0.03, 0.06, 0.12) for L in (4, 8, 15)]
for name, law in [("old full@4 d200", old), ("new full@10 d120", new()), ("new full@8 d100", new(8, 100)),
                  ("new full@12 d150", new(12, 150))]:
    worst = max(sim(law if name.startswith("old") else law, J, L)[0] for J, L in cases)
    res = " ".join(f"{sim(law, J, L)[0]:5.2f}" for J, L in cases)
    print(f"{name:18s} p-p deg per (J,lat): {res}   worst {worst:.2f}")
