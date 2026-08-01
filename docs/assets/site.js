/* Zapret2UI wiki - no dependencies, no build step.
   1) theme toggle           4) copy button on every code block
   2) heading anchors        5) click-to-zoom for screenshots
   3) contents folded into   6) search over a prebuilt index
      the sidebar + spy         (assets/search-index.js) */
(function () {
  'use strict';

  var root = document.documentElement;
  var doc = document.querySelector('.doc');

  // ---------------------------------------------------------------- theme --
  var STORE = 'z2ui-theme';

  // light is the default for reading; dark is a deliberate choice, so the
  // system preference does not decide here
  function currentTheme() {
    return root.getAttribute('data-theme') || 'light';
  }
  function paintToggle(btn) {
    var label = currentTheme() === 'dark' ? 'Светлая' : 'Тёмная';
    btn.textContent = label;
    btn.setAttribute('aria-label', 'Включить тему: ' + label.toLowerCase());
    btn.setAttribute('title', label + ' тема');
  }

  try {
    var saved = localStorage.getItem(STORE);
    if (saved === 'light' || saved === 'dark') root.setAttribute('data-theme', saved);
  } catch (e) { /* private mode: stay on the default */ }

  var toggle = document.querySelector('[data-theme-toggle]');
  if (toggle) {
    paintToggle(toggle);
    toggle.addEventListener('click', function () {
      var next = currentTheme() === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem(STORE, next); } catch (e) { /* nothing to remember it with */ }
      paintToggle(toggle);
    });
  }

  // -------------------------------------------------------------- anchors --
  if (doc) {
    var headings = doc.querySelectorAll('h2[id], h3[id]');
    Array.prototype.forEach.call(headings, function (h) {
      var a = document.createElement('a');
      a.className = 'anchor';
      a.href = '#' + h.id;
      a.textContent = '#';
      a.setAttribute('aria-label', 'Ссылка на раздел «' + h.textContent.trim() + '»');
      h.appendChild(a);
    });
  }

  // ------------------------------------------- contents folded into the nav --
  // The page ships a plain .toc block so it still works without JS. Here it is
  // moved under the active page in the sidebar, making one navigation tree.
  var tocLinks = [];
  var toc = document.querySelector('.toc');
  var activeLink = document.querySelector('.sidebar a[aria-current="page"]');

  if (toc && activeLink && activeLink.parentNode) {
    var list = toc.querySelector('ul');
    if (list) {
      var sub = list.cloneNode(true);
      sub.className = 'subnav';
      activeLink.parentNode.appendChild(sub);
      document.body.classList.add('js-nav');
      tocLinks = sub.querySelectorAll('a[href^="#"]');
    }
  }
  if (!tocLinks.length && toc) tocLinks = toc.querySelectorAll('a[href^="#"]');

  // ------------------------------------------------------------ scroll-spy --
  (function () {
    if (!tocLinks.length || !('IntersectionObserver' in window)) return;

    var byId = {};
    Array.prototype.forEach.call(tocLinks, function (a) {
      byId[a.getAttribute('href').slice(1)] = a;
    });
    var targets = Object.keys(byId)
      .map(function (id) { return document.getElementById(id); })
      .filter(Boolean);
    if (!targets.length) return;

    var visible = Object.create(null);

    function highlight() {
      var pick = null;
      for (var i = 0; i < targets.length; i++) {
        if (visible[targets[i].id]) { pick = targets[i].id; break; }
      }
      if (pick === null) {
        for (var j = targets.length - 1; j >= 0; j--) {
          if (targets[j].getBoundingClientRect().top < 0) { pick = targets[j].id; break; }
        }
      }
      Array.prototype.forEach.call(tocLinks, function (a) { a.classList.remove('active'); });
      if (pick && byId[pick]) byId[pick].classList.add('active');
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) visible[e.target.id] = true;
        else delete visible[e.target.id];
      });
      highlight();
    }, { rootMargin: '-72px 0px -70% 0px', threshold: 0 });

    targets.forEach(function (t) { io.observe(t); });
    highlight();
  })();

  // ------------------------------------------------------- copy code blocks --
  if (doc) {
    Array.prototype.forEach.call(doc.querySelectorAll('pre'), function (pre) {
      var wrap = document.createElement('div');
      wrap.className = 'code-wrap';
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(pre);

      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'copy-btn';
      btn.textContent = 'Копировать';
      wrap.appendChild(btn);

      btn.addEventListener('click', function () {
        var text = pre.innerText;
        function ok() {
          btn.textContent = 'Скопировано';
          btn.classList.add('done');
          setTimeout(function () {
            btn.textContent = 'Копировать';
            btn.classList.remove('done');
          }, 1600);
        }
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(ok, fallback);
        } else {
          fallback();
        }
        // file:// and older browsers have no async clipboard
        function fallback() {
          var ta = document.createElement('textarea');
          ta.value = text;
          ta.setAttribute('readonly', '');
          ta.style.position = 'fixed';
          ta.style.opacity = '0';
          document.body.appendChild(ta);
          ta.select();
          try { document.execCommand('copy'); ok(); } catch (e) { btn.textContent = 'Не вышло'; }
          document.body.removeChild(ta);
        }
      });
    });
  }

  // ------------------------------------------------------------- lightbox ---
  (function () {
    if (!doc) return;
    var figures = doc.querySelectorAll('figure img');
    if (!figures.length) return;

    var box = document.createElement('div');
    box.className = 'lightbox';
    box.innerHTML = '<button type="button" class="lightbox-close" aria-label="Закрыть">✕</button><img alt="">';
    document.body.appendChild(box);
    var big = box.querySelector('img');

    function close() {
      box.classList.remove('on');
      document.body.style.overflow = '';
    }
    box.addEventListener('click', close);
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && box.classList.contains('on')) close();
    });

    Array.prototype.forEach.call(figures, function (img) {
      var holder = document.createElement('span');
      holder.className = 'zoomable';
      holder.setAttribute('role', 'button');
      holder.setAttribute('tabindex', '0');
      holder.setAttribute('aria-label', 'Открыть изображение крупно');
      img.parentNode.insertBefore(holder, img);
      holder.appendChild(img);

      function open() {
        big.src = img.currentSrc || img.src;
        big.alt = img.alt;
        box.classList.add('on');
        document.body.style.overflow = 'hidden';
      }
      holder.addEventListener('click', open);
      holder.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
      });
    });
  })();

  // --------------------------------------------------------------- search ---
  (function () {
    var btn = document.querySelector('[data-search-open]');
    var index = window.SEARCH_INDEX;
    if (!btn) return;
    if (!index || !index.length) { btn.style.display = 'none'; return; }

    var modal = document.createElement('div');
    modal.className = 'search-modal';
    modal.innerHTML =
      '<div class="search-backdrop"></div>' +
      '<div class="search-panel" role="dialog" aria-modal="true" aria-label="Поиск по документации">' +
        '<input type="search" placeholder="Поиск по документации" autocomplete="off" spellcheck="false">' +
        '<ul class="search-results"></ul>' +
        '<div class="search-hint"><span>Enter открыть</span><span>Стрелки листать</span><span>Esc закрыть</span></div>' +
      '</div>';
    document.body.appendChild(modal);

    var input = modal.querySelector('input');
    var results = modal.querySelector('.search-results');
    var sel = 0;
    var hits = [];

    function esc(s) {
      return s.replace(/[&<>"]/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
      });
    }
    function mark(text, q) {
      var i = text.toLowerCase().indexOf(q);
      if (i < 0) return esc(text);
      return esc(text.slice(0, i)) + '<mark>' + esc(text.slice(i, i + q.length)) +
             '</mark>' + esc(text.slice(i + q.length));
    }
    // show the part of the snippet that actually contains the query
    function around(text, q) {
      var i = text.toLowerCase().indexOf(q);
      if (i < 0) return text.slice(0, 120) + (text.length > 120 ? '…' : '');
      var from = Math.max(0, i - 45);
      var out = text.slice(from, from + 150);
      return (from > 0 ? '…' : '') + out + (from + 150 < text.length ? '…' : '');
    }

    function render(q) {
      results.innerHTML = '';
      sel = 0;
      if (!q) {
        results.innerHTML = '<li class="search-empty">Введите слово: например, «голос», «QUIC» или «hostfakesplit».</li>';
        hits = [];
        return;
      }
      hits = [];
      for (var i = 0; i < index.length; i++) {
        var e = index[i];
        var inTitle = e.t.toLowerCase().indexOf(q);
        var inBody = e.s.toLowerCase().indexOf(q);
        if (inTitle < 0 && inBody < 0) continue;
        hits.push({ e: e, score: inTitle >= 0 ? inTitle : 1000 + inBody });
      }
      hits.sort(function (a, b) { return a.score - b.score; });
      hits = hits.slice(0, 14);

      if (!hits.length) {
        results.innerHTML = '<li class="search-empty">Ничего не нашлось.</li>';
        return;
      }
      hits.forEach(function (h, n) {
        var e = h.e;
        var li = document.createElement('li');
        li.innerHTML =
          '<a href="' + e.p + (e.id ? '#' + e.id : '') + '"' + (n === 0 ? ' class="sel"' : '') + '>' +
            '<span class="r-page">' + esc(e.pt) + '</span>' +
            '<span class="r-title">' + mark(e.t, q) + '</span>' +
            '<span class="r-snip">' + mark(around(e.s, q), q) + '</span>' +
          '</a>';
        results.appendChild(li);
      });
    }

    function move(step) {
      var links = results.querySelectorAll('a');
      if (!links.length) return;
      links[sel].classList.remove('sel');
      sel = (sel + step + links.length) % links.length;
      links[sel].classList.add('sel');
      links[sel].scrollIntoView({ block: 'nearest' });
    }

    function open() {
      modal.classList.add('on');
      document.body.style.overflow = 'hidden';
      input.value = '';
      render('');
      input.focus();
    }
    function close() {
      modal.classList.remove('on');
      document.body.style.overflow = '';
    }

    btn.addEventListener('click', open);
    modal.querySelector('.search-backdrop').addEventListener('click', close);
    input.addEventListener('input', function () { render(input.value.trim().toLowerCase()); });

    modal.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
      else if (e.key === 'Enter') {
        var links = results.querySelectorAll('a');
        if (links.length) { e.preventDefault(); links[sel].click(); }
      }
    });

    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && modal.classList.contains('on')) { close(); return; }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); open(); }
      // "/" opens search unless the user is typing somewhere
      if (e.key === '/' && !modal.classList.contains('on')) {
        var t = e.target.tagName;
        if (t !== 'INPUT' && t !== 'TEXTAREA' && !e.target.isContentEditable) { e.preventDefault(); open(); }
      }
    });
  })();
})();
