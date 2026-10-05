const root = document.documentElement;
const themeButton = document.querySelector('.theme');
function setTheme(theme) {
  root.dataset.theme = theme;
  themeButton.textContent = theme === 'dark' ? 'Light' : 'Dark';
  themeButton.setAttribute('aria-label', `Switch to ${theme === 'dark' ? 'light' : 'dark'} theme`);
  document.querySelector('.panel-shot').src = `assets/widget-demo-${theme}.png`;
  document.querySelector('.panel-shot').alt = `Display Regent glass widget with line-art monitor frames, three preset boxes and a Full panel link, in ${theme} mode`;
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#141c2b' : '#f4f6fa';
}
try { setTheme(localStorage.getItem('regent-theme') === 'light' ? 'light' : 'dark'); } catch {}
themeButton.addEventListener('click', () => { const theme = root.dataset.theme === 'dark' ? 'light' : 'dark'; setTheme(theme); try { localStorage.setItem('regent-theme', theme); } catch {} });
const monitors = [...document.querySelectorAll('[data-screen]')];
const status = document.querySelector('#demo-status');
function turn(screen, on) { screen.setAttribute('aria-pressed', String(on)); screen.querySelector('em').textContent = on ? (screen.dataset.screen === '1' ? '✦ Primary' : 'On') : 'Off'; }
monitors.forEach(screen => screen.addEventListener('click', () => {
  const on = screen.getAttribute('aria-pressed') !== 'true';
  if (!on && monitors.filter(m => m.getAttribute('aria-pressed') === 'true').length === 1) { status.textContent = 'At least one screen stays on.'; return; }
  turn(screen, on);
  document.querySelectorAll('[data-scene]').forEach(b => b.classList.remove('active'));
  status.textContent = `Display ${screen.dataset.screen} is ${on ? 'on' : 'off'} in this sample. Your PC is unchanged.`;
}));
document.querySelectorAll('[data-scene]').forEach(button => button.addEventListener('click', () => {
  document.querySelectorAll('[data-scene]').forEach(b => b.classList.toggle('active', b === button));
  monitors.forEach(m => turn(m, button.dataset.scene === 'all' || m.dataset.screen === '1' || (button.dataset.scene === 'stacked' && m.dataset.screen === '3')));
  status.textContent = `${({all:'Everyday',focus:'Focus',stacked:'Stacked'})[button.dataset.scene]} scene selected in this sample. Your PC is unchanged.`;
}));

const shot = document.querySelector('.panel-shot');
shot.addEventListener('mouseenter', () => { shot.src = `assets/widget-demo-hover-${root.dataset.theme}.png`; });
shot.addEventListener('mouseleave', () => { shot.src = `assets/widget-demo-${root.dataset.theme}.png`; });
