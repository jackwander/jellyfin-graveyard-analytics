/*
 * The "Leaving Soon" home screen row.
 *
 * Injected into index.html by HomeSectionStartupFilter, and only when the admin has switched
 * the feature on. Everything here is best-effort by design: Jellyfin has no supported API for
 * adding a home section, so this reads the DOM the web client produces and appends a row to it.
 *
 * The rule that shapes the whole file: a failure here must cost the row and nothing else. No
 * exception may escape, and nothing existing is modified or removed. If a Jellyfin update
 * changes the home screen, the worst outcome is that the row stops appearing.
 *
 * "No exception may escape" is stronger than it sounds, and the first release of this file got
 * it wrong in a way no test caught. watch() called render() before installing the observer,
 * and render() asked ApiClient for the current user without a guard. On a page load with no
 * session yet that call throws, the exception escaped watch(), start()'s catch swallowed it,
 * and the observer was never installed — so the feature did nothing for the rest of the
 * session on a server where every other part of it worked. Ordering matters here: install the
 * observer first, then try to render.
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

    /**
     * The signed-in user, or null if there is not one yet.
     *
     * Guarded because it is called on every DOM mutation from the moment the page loads, and
     * early in that life the client has no session — `getCurrentUserId` can throw rather than
     * return nothing. An exception escaping here used to kill the whole feature for the rest
     * of the session; see watch().
     */
    function currentUserId() {
        try {
            if (!window.ApiClient || typeof window.ApiClient.getItems !== 'function') return null;
            return window.ApiClient.getCurrentUserId() || null;
        } catch (err) {
            return null;
        }
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
    // Built out of the web client's own card classes rather than a bespoke card. Hand-rolled
    // markup sat flush against the page edge while every native row was indented, and the
    // posters came out half size — reusing `card overflowPortraitCard` and friends inherits
    // the sizing, spacing, hover and focus behaviour instead of approximating them.
    //
    // The `graveyard-leaving-*` classes are kept alongside as stable hooks: they are what the
    // harness asserts on, and they are ours to rename, whereas the Jellyfin ones are not.
    function buildCard(item) {
        var card = document.createElement('div');
        card.className = 'card overflowPortraitCard card-hoverable graveyard-leaving-item';

        var box = document.createElement('div');
        box.className = 'cardBox cardBox-bottompadded';

        var scalable = document.createElement('div');
        scalable.className = 'cardScalable';

        // Gives the card its aspect ratio; Jellyfin's own rows do exactly this.
        var padder = document.createElement('div');
        padder.className = 'cardPadder cardPadder-overflowPortrait';
        scalable.appendChild(padder);

        var link = document.createElement('a');
        link.className = 'cardImageContainer coveredImage cardContent itemAction graveyard-leaving-card';
        link.href = '#/details?id=' + encodeURIComponent(item.Id);
        link.title = item.Name || '';

        try {
            var url = window.ApiClient.getImageUrl(item.Id, { type: 'Primary', maxHeight: 300 });
            if (url) link.style.backgroundImage = 'url("' + url.replace(/"/g, '%22') + '")';
        } catch (err) {
            log('no artwork for an item', err);
        }

        scalable.appendChild(link);
        box.appendChild(scalable);

        var name = document.createElement('div');
        name.className = 'cardText cardTextCentered cardText-first graveyard-leaving-name';
        name.textContent = item.Name || 'Unknown';
        box.appendChild(name);

        card.appendChild(box);
        return card;
    }

    function buildSection(collection, items) {
        var section = document.createElement('div');
        section.id = SECTION_ID;
        section.className = 'verticalSection graveyard-leaving-section';

        // padded-left is what aligns the heading with every other row on the page. Without it
        // the section starts at the viewport edge while the native ones are indented, which is
        // what made this look bolted on rather than built in.
        var titleRow = document.createElement('div');
        titleRow.className = 'sectionTitleContainer sectionTitleContainer-cards padded-left';

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle sectionTitle-cards graveyard-leaving-title';
        heading.textContent = 'Leaving Soon';
        titleRow.appendChild(heading);

        // The row is a teaser; the collection is the full list. It sits beside the heading the
        // way Jellyfin's own "see all" affordances do, rather than as a bare link trailing off
        // the bottom of the section.
        var more = document.createElement('a');
        more.className = 'graveyard-leaving-more';
        more.href = '#/details?id=' + encodeURIComponent(collection.Id);
        more.textContent = 'See all';
        titleRow.appendChild(more);

        section.appendChild(titleRow);

        var strip = document.createElement('div');
        strip.className = 'itemsContainer padded-left padded-right graveyard-leaving-strip';
        items.forEach(function (item) { strip.appendChild(buildCard(item)); });
        section.appendChild(strip);

        return section;
    }

    function injectStyles() {
        if (document.getElementById('graveyardLeavingStyles')) return;
        var style = document.createElement('style');
        style.id = 'graveyardLeavingStyles';
        // Deliberately thin. Sizing, spacing, hover and focus all come from the web client's
        // own card rules now; anything restated here would only drift from them. What is left
        // is the horizontal scroll (Jellyfin's rows get that from a scroller component this
        // does not use) and the placement of the "see all" link.
        style.textContent = [
            '.graveyard-leaving-strip{display:flex;overflow-x:auto;overflow-y:hidden;}',
            '.graveyard-leaving-strip > .card{flex:0 0 auto;}',
            '.graveyard-leaving-section .sectionTitleContainer{display:flex;align-items:baseline;}',
            '.graveyard-leaving-more{margin-left:1em;font-size:.85em;opacity:.75;text-decoration:none;}',
            '.graveyard-leaving-more:hover{opacity:1;text-decoration:underline;}'
        ].join('');
        document.head.appendChild(style);
    }

    var running = false;

    function render() {
        if (running) return Promise.resolve();
        if (document.getElementById(SECTION_ID)) return Promise.resolve();

        var container = findContainer();
        if (!container) return Promise.resolve();

        var userId = currentUserId();
        if (!userId) return Promise.resolve();

        running = true;

        // The prologue is inside the try as well: a synchronous throw here used to escape
        // render() with `running` still true, which latched the flag and made every later
        // attempt return immediately — one transient error became a permanent one.
        try {
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
        } catch (err) {
            running = false;
            log('render failed before it could start', err);
            return Promise.resolve();
        }
    }

    function attempt() {
        try {
            render();
        } catch (err) {
            // render() should never throw, but a caller that assumes so is how this feature
            // died once already.
            log('render threw', err);
        }
    }

    // The home screen is rendered client-side and re-rendered on navigation, so there is no
    // single moment to hook — the only reliable trigger is watching the document.
    //
    // The observer is installed *before* the first render attempt, and that ordering is the
    // whole point. It used to be the other way round, and `ready()` called
    // `ApiClient.getCurrentUserId()` unguarded: on a page load with no session yet that threw,
    // the exception escaped watch(), start()'s catch swallowed it, and the observer was never
    // installed at all. The feature then did nothing for the rest of the session, silently,
    // on a server where every other part of it worked.
    //
    // There is also no longer a timeout. Disconnecting after 30 seconds assumed the home
    // screen always appears inside that window, which is untrue on a slow load or behind a
    // login. The callback is cheap — it early-returns on an existing row — so observing for
    // the life of the page costs little, and it also re-adds the row if the client re-renders
    // the container out from under it.
    var observing = false;

    function watch() {
        if (!observing) {
            try {
                new MutationObserver(debounced).observe(document.body, {
                    childList: true,
                    subtree: true
                });
                observing = true;
            } catch (err) {
                log('could not observe the document', err);
            }
        }

        attempt();
    }

    var pending = null;

    function debounced() {
        if (pending) return;
        pending = setTimeout(function () {
            pending = null;
            attempt();
        }, 150);
    }

    function start() {
        try {
            watch();
        } catch (err) {
            log('startup failed', err);
        }

        // Navigating back to the home screen rebuilds the container; the observer catches
        // that, and this is belt and braces for clients that swap views without mutating body.
        try {
            window.addEventListener('hashchange', function () { setTimeout(watch, 300); });
        } catch (err) {
            log('could not listen for navigation', err);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
