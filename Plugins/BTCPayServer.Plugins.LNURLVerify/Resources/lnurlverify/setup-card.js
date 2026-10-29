// An LNURL as the Lightning setup page and the LNURL rails page show it: one switch per rail and per token asset,
// a token's networks as its icons, dimmed where checkout cannot offer them.
window.LnurlSetupCard = (() => {
    const el = (tag, className, text) => {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text != null) e.textContent = text;
        return e;
    };
    const sats = msat => msat == null ? '?' : (msat / 1000).toLocaleString();
    const range = o => sats(o.minSendable) + ' – ' + sats(o.maxSendable) + ' sats';

    function row (opts, lead, options, ...label) {
        const icon = (name, alt) => Object.assign(el('img'), { src: opts.iconUrl.replace('{0}', name), alt });
        const r = el('div', 'lnurl-setup-row');
        const locked = options.every(o => o.locked) ? lead.locked : null;
        const sw = el('input', 'form-check-input');
        sw.type = 'checkbox';
        sw.setAttribute('role', 'switch');
        sw.setAttribute('aria-label', label.map(l => l.textContent || l.alt).join(' '));
        sw.checked = !locked && !lead.excluded;
        sw.disabled = !lead.toggle || !!locked;
        const toggle = el('div', 'form-check form-switch');
        toggle.append(sw);
        if (lead.toggle && !locked) {
            // Form fields too, so a page that posts its form saves the card as it stands.
            Object.assign(sw, { name: 'enabledRails', value: lead.toggle });
            sw.dataset.toggle = lead.toggle;
            if (opts.onChange) sw.addEventListener('change', opts.onChange);
            toggle.append(Object.assign(el('input'), { type: 'hidden', name: 'shownRails', value: lead.toggle }));
        }
        const end = el('span', 'lnurl-setup-end');
        if (lead.unit && lead.toggle && !locked) {
            end.classList.add('lnurl-setup-nets');
            for (const o of options) {
                const why = o.locked || o.transient;
                const net = o.icon ? icon(o.icon, o.label) : el('span', null, o.label);
                net.title = o.label + (why ? ': ' + why : '');
                net.tabIndex = 0;
                net.dataset.option = o.id;
                if (o.locked) net.classList.add('off');
                end.append(net);
            }
        } else {
            end.textContent = locked || (lead.excluded ? '' : lead.transient) || '';
        }
        r.append(toggle, ...label, end);
        return r;
    }

    function render (out, s, opts) {
        const tooltips = window.bootstrap && window.bootstrap.Tooltip;
        if (tooltips) out.querySelectorAll('[data-option]').forEach(e => tooltips.getInstance(e)?.dispose());
        out.replaceChildren();
        if (s.error) {
            out.append(el('p', 'text-danger mb-0', s.error));
            return;
        }
        const card = el('div', 'lnurl-setup border rounded');
        const head = el('div', 'lnurl-setup-head');
        head.append(el('strong', null, s.domain), el('span', 'text-muted', (s.sends ? 'Sends and receives' : 'Receives only') + ' · ' + range(s)));
        card.append(head);
        const units = new Map();
        for (const o of s.options) {
            if (o.unit && o.toggle) units.set(o.unit, [...(units.get(o.unit) || []), o]);
            else card.append(Object.assign(row(opts, o, [o], el('span', null, o.label)), { id: 'LNURLVerifyOption-' + (o.id || 'lightning') }));
        }
        // Networks every token offers lead each token's row, in the first token's order, so the rows line up.
        const chain = o => o.asset ? o.asset.split('/')[0] : o.icon || o.label;
        const lists = [...units.values()];
        const shared = lists.length ? lists[0].map(chain).filter(c => lists.every(l => l.some(o => chain(o) === c))) : [];
        const rank = o => shared.includes(chain(o)) ? shared.indexOf(chain(o)) : shared.length;
        for (const [unit, unsorted] of units) {
            const options = [...unsorted].sort((a, b) => rank(a) - rank(b));
            const name = el('span', 'lnurl-setup-unit', unit);
            const logo = options[0].unitIcon && Object.assign(el('img'), { src: opts.iconUrl.replace('{0}', options[0].unitIcon), alt: '' });
            card.append(Object.assign(logo ? row(opts, options[0], options, logo, name) : row(opts, options[0], options, name),
                { id: 'LNURLVerifyUnit-' + unit }));
        }
        out.append(card);
        if (tooltips) out.querySelectorAll('[data-option]').forEach(e => tooltips.getOrCreateInstance(e));
    }

    return { render };
})();
