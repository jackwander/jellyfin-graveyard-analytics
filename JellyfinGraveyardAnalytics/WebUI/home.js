/*
 * The "Leaving Soon" home screen row.
 *
 * Injected into index.html by HomeSectionStartupFilter, and only when the admin has switched
 * the feature on. Everything here is best-effort by design: Jellyfin has no supported API for
 * adding a home section, so this reads the DOM the web client produces and appends a row to it.
 *
 * The rule that shapes the whole file: a failure here must cost the row and nothing else. No
 * exception may escape, nothing existing is modified or removed, and the observer disconnects
 * itself rather than running forever. If a Jellyfin update changes the home screen, the worst
 * outcome is that the row stops appearing.
 *
 * Deliberately NOT what the community plugins do — they splice code into the minified webpack
 * bundle and hardcode identifiers per Jellyfin version ("h" on 10.10.7, "u" on 10.11). That
 * buys a real section, at the price of breaking on every release.
 */
(function () {
    'use strict';

    var SECTION_ID = 'graveyardLeavingSoonSection';
    var COLLECTION_NAME = 'Leaving Soon: The Chapel';
    var MAX_ITEMS = 20;
    var OBSERVER_TIMEOUT_MS = 30000;

    // The container the home screen renders its sections into. Checked in order; the first
    // that matches wins, so a renamed class in one release does not have to break this.
    var CONTAINER_SELECTORS = ['.homeSectionsContainer', '.homeSections', '.sections'];

    function log(message, error) {
        // Quiet by default. A row that does not appear should not fill anyone's console.
        if (window.GRAVEYARD_HOME_DEBUG) console.warn('[Graveyard] ' + message, error || '');
    }

    function findContainer() {
        for (var i = 0; i < CONTAINER_SELECTORS.length; i++) {
            var el = document.querySelector(CONTAINER_SELECTORS[i]);
            if (el) return el;
        }
        return null;
    }

    function ready() {
        return typeof window.ApiClient !== 'undefined'
            && window.ApiClient
            && typeof window.ApiClient.getItems === 'function'
            && !!window.ApiClient.getCurrentUserId();
    }

    /**
     * The Chapel collection, or null. Matched by exact name because that is the contract the
     * server side keeps — ChapelCollectionName in GraveyardAnalyticsController.
     */
    function findCollection(userId) {
        return window.ApiClient.getItems(userId, {
            IncludeItemTypes: 'BoxSet',
            Recursive: true,
            SearchTerm: COLLECTION_NAME
        }).then(function (result) {
            var items = (result && result.Items) || [];
            for (var i = 0; i < items.length; i++) {
                if (items[i].Name === COLLECTION_NAME) return items[i];
            }
            return null;
        });
    }

    function fetchCondemned(userId, collectionId) {
        return window.ApiClient.getItems(userId, {
            ParentId: collectionId,
            Limit: MAX_ITEMS,
            Fields: 'PrimaryImageAspectRatio',
            SortBy: 'SortName'
        }).then(function (result) {
            return (result && result.Items) || [];
        });
    }

    // Titles come from filenames and are attacker-influenced, so every one of these is
    // textContent or an attribute set through the DOM API. The same rule the admin dashboard
    // follows; there is no innerHTML in this file.
    function buildCard(item) {
        var link = document.createElement('a');
        link.className = 'graveyard-leaving-card';
        link.href = '#/details?id=' + encodeURIComponent(item.Id);
        link.title = item.Name || '';

        var art = document.createElement('div');
        art.className = 'graveyard-leaving-art';
        try {
            var url = window.ApiClient.getImageUrl(item.Id, { type: 'Primary', maxHeight: 300 });
            if (url) art.style.backgroundImage = 'url("' + url.replace(/"/g, '%22') + '")';
        } catch (err) {
            log('no artwork for an item', err);
        }
        link.appendChild(art);

        var name = document.createElement('div');
        name.className = 'graveyard-leaving-name';
        name.textContent = item.Name || 'Unknown';
        link.appendChild(name);

        return link;
    }

    function buildSection(collection, items) {
        var section = document.createElement('div');
        section.id = SECTION_ID;
        section.className = 'verticalSection graveyard-leaving-section';

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle graveyard-leaving-title';
        heading.textContent = 'Leaving Soon';
        section.appendChild(heading);

        var strip = document.createElement('div');
        strip.className = 'graveyard-leaving-strip';
        items.forEach(function (item) { strip.appendChild(buildCard(item)); });
        section.appendChild(strip);

        // The row is a teaser; the collection is the full list.
        var more = document.createElement('a');
        more.className = 'graveyard-leaving-more';
        more.href = '#/details?id=' + encodeURIComponent(collection.Id);
        more.textContent = 'See everything in The Chapel';
        section.appendChild(more);

        return section;
    }

    function injectStyles() {
        if (document.getElementById('graveyardLeavingStyles')) return;
        var style = document.createElement('style');
        style.id = 'graveyardLeavingStyles';
        style.textContent = [
            '.graveyard-leaving-section{margin:0 0 2em;}',
            '.graveyard-leaving-title{margin-bottom:.4em;}',
            '.graveyard-leaving-strip{display:flex;gap:12px;overflow-x:auto;padding-bottom:6px;}',
            '.graveyard-leaving-card{flex:0 0 auto;width:140px;text-decoration:none;color:inherit;}',
            '.graveyard-leaving-art{width:140px;height:210px;border-radius:6px;background:#222 center/cover no-repeat;border:1px solid #333;}',
            '.graveyard-leaving-name{margin-top:.4em;font-size:.85em;line-height:1.25;overflow:hidden;' +
                'display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;}',
            '.graveyard-leaving-more{display:inline-block;margin-top:.6em;font-size:.85em;opacity:.75;}'
        ].join('');
        document.head.appendChild(style);
    }

    var running = false;

    function render() {
        if (running) return Promise.resolve();
        if (document.getElementById(SECTION_ID)) return Promise.resolve();

        var container = findContainer();
        if (!container || !ready()) return Promise.resolve();

        running = true;
        var userId = window.ApiClient.getCurrentUserId();

        return findCollection(userId)
            .then(function (collection) {
                // No collection means nothing has ever been condemned.
                if (!collection) return null;
                return fetchCondemned(userId, collection.Id).then(function (items) {
                    return { collection: collection, items: items };
                });
            })
            .then(function (data) {
                // An empty Chapel renders nothing at all, rather than an empty row announcing
                // that nothing is leaving. Requested behaviour, and the right default.
                if (!data || !data.items.length) return;
                if (document.getElementById(SECTION_ID)) return;

                var host = findContainer();
                if (!host) return;

                injectStyles();
                host.appendChild(buildSection(data.collection, data.items));
            })
            .catch(function (err) {
                log('could not build the Leaving Soon row', err);
            })
            .then(function () { running = false; });
    }

    // The home screen is rendered client-side and re-rendered on navigation, so there is no
    // single moment to hook. Watch for the container, give up after a while rather than
    // observing the document for the life of the session, and re-arm on navigation.
    function watch() {
        render();

        var observer = new MutationObserver(function () { render(); });
        try {
            observer.observe(document.body, { childList: true, subtree: true });
        } catch (err) {
            log('could not observe the document', err);
            return;
        }

        setTimeout(function () { observer.disconnect(); }, OBSERVER_TIMEOUT_MS);
    }

    function start() {
        try {
            watch();
            window.addEventListener('hashchange', function () {
                // Cheap re-arm: navigating back to the home screen rebuilds the container.
                setTimeout(watch, 300);
            });
        } catch (err) {
            log('startup failed', err);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
