// Shell behaviour shared by every page: the navigation toggle and the live
// gateway indicator in the sidebar.
//
// Deliberately small. Each page keeps its own key helpers and its own fetch
// code, because those pages declare them at the top level and a second
// declaration of the same name here would be a syntax error that takes the
// whole script down with it. Everything in this file is namespaced under one
// object for the same reason.

const PandaShell = {
  toggleNav() {
    document.body.classList.toggle('nav-open');
  },

  // The gateway's own liveness, which is deliberately not an aggregate of the
  // services behind it: a gateway that reported itself unhealthy because one
  // service was down would be pulled out of rotation exactly when it could
  // still route to the others.
  async pollHealth() {
    const dot = document.getElementById('healthDot');
    const text = document.getElementById('healthText');
    if (!dot || !text) return;

    try {
      const res = await fetch('/health', { cache: 'no-store' });
      if (!res.ok) throw new Error(String(res.status));
      dot.className = 'dot up';
      text.textContent = 'gateway healthy, port 5000';
    } catch {
      dot.className = 'dot down';
      text.textContent = 'gateway unreachable';
    }
  },

  // Closing the drawer on a navigation keeps a tap on a phone from leaving the
  // overlay up over the page that was just opened.
  init() {
    document.querySelectorAll('.nav-item').forEach(a =>
      a.addEventListener('click', () => document.body.classList.remove('nav-open')));

    PandaShell.pollHealth();
    setInterval(PandaShell.pollHealth, 10000);
  }
};

document.addEventListener('DOMContentLoaded', PandaShell.init);
