(() => {
 'use strict';
 for (const chart of document.querySelectorAll('[data-chart]')) {
  let rows;
  try { rows = JSON.parse(chart.dataset.chart); } catch { continue; }
  const svg = chart.querySelector('svg');
  const series = svg?.querySelector('polyline');
  if (!Array.isArray(rows) || rows.length < 2 || series?.points.numberOfItems !== rows.length || !rows.every(r => typeof r.date === 'string' && Number.isFinite(r.price))) continue;
  const ns = 'http://www.w3.org/2000/svg';
  const guide = document.createElementNS(ns, 'line');
  guide.classList.add('chart-crosshair');
  guide.setAttribute('y1', '10'); guide.setAttribute('y2', '160');
  const dot = document.createElementNS(ns, 'circle');
  dot.classList.add('chart-point'); dot.setAttribute('r', '4');
  const overlay = document.createElementNS(ns, 'g');
  overlay.classList.add('chart-tracker'); overlay.style.display = 'none';
  overlay.append(guide, dot); svg.append(overlay);
  const tooltip = document.createElement('div');
  tooltip.className = 'chart-tooltip'; tooltip.hidden = true;
  tooltip.setAttribute('role', 'tooltip');
  const date = document.createElement('span');
  const price = document.createElement('strong');
  const label = document.createElement('small');
  label.textContent = chart.dataset.priceLabel || 'Close';
  tooltip.append(date, price, label); chart.append(tooltip);
  const format = new Intl.NumberFormat(undefined, {minimumFractionDigits:2, maximumFractionDigits:4});
  let selected = rows.length - 1;
  function show(index) {
   selected = Math.max(0, Math.min(rows.length - 1, index));
   const row = rows[selected]; const point = series.points.getItem(selected);
   date.textContent = row.date;
   price.textContent = (chart.dataset.currency || '') + ' ' + format.format(row.price);
   chart.setAttribute('aria-label', `${row.date}, ${label.textContent}, ${price.textContent}. Use Left and Right arrow keys to explore prices.`);
   guide.setAttribute('x1', point.x); guide.setAttribute('x2', point.x);
   dot.setAttribute('cx', point.x); dot.setAttribute('cy', point.y);
   overlay.style.display = ''; tooltip.hidden = false;
   const matrix = svg.getScreenCTM(); if (!matrix) return;
   const screen = new DOMPoint(point.x, point.y).matrixTransform(matrix);
   const bounds = chart.getBoundingClientRect();
   // Keep the tooltip within the chart, including the first and last observations.
   const left = Math.max(8, Math.min(bounds.width - tooltip.offsetWidth - 8, screen.x - bounds.left - tooltip.offsetWidth / 2));
   tooltip.style.left = `${left}px`;
   tooltip.style.top = `${Math.max(0, screen.y - bounds.top - tooltip.offsetHeight - 12)}px`;
  }
  function hide() { tooltip.hidden = true; overlay.style.display = 'none'; }
  function track(event) {
   const matrix = svg.getScreenCTM(); if (!matrix) return;
   const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
   const first = series.points.getItem(0).x;
   const last = series.points.getItem(rows.length - 1).x;
   if (last <= first) return;
   show(Math.round((point.x - first) / (last - first) * (rows.length - 1)));
  }
  chart.addEventListener('pointermove', track);
  chart.addEventListener('pointerdown', event => {
   track(event);
   if (event.pointerType === 'touch') chart.setPointerCapture(event.pointerId);
  });
  chart.addEventListener('pointerleave', event => { if (event.pointerType !== 'touch') hide(); });
  chart.addEventListener('pointercancel', hide);
  chart.addEventListener('focus', () => { if (chart.matches(':focus-visible')) show(selected); });
  chart.addEventListener('blur', hide);
  chart.addEventListener('keydown', event => {
   if (!['ArrowLeft','ArrowRight','Home','End','Escape'].includes(event.key)) return;
   event.preventDefault();
   if (event.key === 'Escape') hide();
   else show(event.key === 'Home' ? 0 : event.key === 'End' ? rows.length - 1 : selected + (event.key === 'ArrowLeft' ? -1 : 1));
  });
  document.addEventListener('pointerdown', event => { if (!chart.contains(event.target)) hide(); });
 }
})();
