(function () {
    'use strict';

    if (window.__ppsInitialized) {
        return;
    }
    window.__ppsInitialized = true;

    var PLUGIN_PATH = 'PostPlaySuggestions';
    var VERSION = '0.5.1.0';

    var state = {
        config: {
            enabled: true,
            maxSuggestions: 6,
            triggerSecondsBeforeEnd: 20,
            showOnSeriesFinaleOnly: true
        },
        video: null,
        playerHost: null,
        item: null,
        sessionId: null,
        activated: false,
        activationPending: false,
        dismissed: false,
        wasFullscreen: false,
        keepFullscreen: false,
        overlay: null,
        suggestions: [],
        lastNowPlayingCheck: 0,
        nowPlayingPromise: null,
        lastConfigCheck: 0,
        finaleCache: {},
        creditsStart: null,
        creditsItemId: null,
        creditsSource: null,
        creditsAttempts: 0,
        creditsLastAttempt: 0,
        creditsPendingItemId: null,
        themeTimer: null
    };

    function basePath() {
        var path = window.location.pathname || '';
        var webIndex = path.toLowerCase().indexOf('/web');
        if (webIndex >= 0) {
            return path.substring(0, webIndex);
        }

        var lastSlash = path.lastIndexOf('/');
        return lastSlash > 0 ? path.substring(0, lastSlash) : '';
    }

    function pluginUrl(path, params) {
        var url = (basePath() || '') + '/' + PLUGIN_PATH + '/' + String(path).replace(/^\/+/, '');
        if (params) {
            var query = [];
            Object.keys(params).forEach(function (key) {
                var value = params[key];
                if (value === undefined || value === null || value === '') {
                    return;
                }
                query.push(encodeURIComponent(key) + '=' + encodeURIComponent(value));
            });
            if (query.length) {
                url += (url.indexOf('?') >= 0 ? '&' : '?') + query.join('&');
            }
        }
        return url;
    }

    function apiUrl(path, params) {
        if (window.ApiClient && typeof ApiClient.getUrl === 'function') {
            try {
                return ApiClient.getUrl(PLUGIN_PATH + '/' + String(path).replace(/^\/+/, ''), params);
            } catch (error) {
                // Fall through to a relative URL.
            }
        }
        return pluginUrl(path, params);
    }

    function isLoggedIn() {
        try {
            return !!(window.ApiClient && ApiClient.getCurrentUserId && ApiClient.getCurrentUserId());
        } catch (error) {
            return false;
        }
    }

    function currentUserId() {
        try {
            return ApiClient.getCurrentUserId();
        } catch (error) {
            return null;
        }
    }

    function deviceId() {
        try {
            return ApiClient.deviceId ? ApiClient.deviceId() : '';
        } catch (error) {
            return '';
        }
    }

    function getJson(url) {
        if (window.ApiClient && typeof ApiClient.getJSON === 'function') {
            return ApiClient.getJSON(url);
        }

        return fetch(url, { credentials: 'same-origin', cache: 'no-store' }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function reportPanel(active) {
        if (!state.sessionId || !window.ApiClient || typeof ApiClient.ajax !== 'function') {
            return;
        }

        try {
            var url = ApiClient.getUrl(PLUGIN_PATH + '/Panel', {
                sessionId: state.sessionId,
                active: active ? 'true' : 'false'
            });
            ApiClient.ajax({ type: 'POST', url: url });
        } catch (error) {
            // Reporting is best-effort: the overlay keeps working without it.
        }
    }

    function normalizeConfig(payload) {
        var source = payload || {};
        var maxSuggestions = parseInt(source.maxSuggestions, 10);
        var triggerSeconds = parseInt(source.triggerSecondsBeforeEnd, 10);

        return {
            enabled: source.enabled !== false,
            maxSuggestions: isNaN(maxSuggestions) ? 6 : Math.max(1, Math.min(12, maxSuggestions)),
            triggerSecondsBeforeEnd: isNaN(triggerSeconds) ? 20 : Math.max(1, Math.min(300, triggerSeconds)),
            showOnSeriesFinaleOnly: source.showOnSeriesFinaleOnly !== false
        };
    }

    function fetchConfig() {
        state.lastConfigCheck = Date.now();
        return fetch(pluginUrl('Config'), { credentials: 'same-origin', cache: 'no-store' })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error('HTTP ' + response.status);
                }
                return response.json();
            })
            .then(function (payload) {
                state.config = normalizeConfig(payload);
                if (!state.config.enabled && state.activated) {
                    closeSuggestions('disabled');
                }
            })
            .catch(function () {
                // Keep the previous configuration if the request fails.
            });
    }

    function readValue(object, name) {
        if (!object) {
            return undefined;
        }
        if (object[name] !== undefined && object[name] !== null) {
            return object[name];
        }
        var camel = name.charAt(0).toLowerCase() + name.slice(1);
        return object[camel];
    }

    function normalizeNowPlaying(payload) {
        if (!payload) {
            return null;
        }

        var itemId = readValue(payload, 'itemId');
        if (!itemId) {
            return null;
        }

        return {
            sessionId: readValue(payload, 'sessionId'),
            itemId: itemId,
            itemType: readValue(payload, 'itemType') || '',
            name: readValue(payload, 'name') || '',
            seriesId: readValue(payload, 'seriesId'),
            seriesName: readValue(payload, 'seriesName'),
            seasonId: readValue(payload, 'seasonId'),
            seasonName: readValue(payload, 'seasonName'),
            indexNumber: readValue(payload, 'indexNumber'),
            parentIndexNumber: readValue(payload, 'parentIndexNumber'),
            runTimeTicks: readValue(payload, 'runTimeTicks'),
            positionTicks: readValue(payload, 'positionTicks'),
            isPaused: readValue(payload, 'isPaused') === true,
            productionYear: readValue(payload, 'productionYear'),
            communityRating: readValue(payload, 'communityRating'),
            overview: readValue(payload, 'overview') || '',
            primaryImageTag: readValue(payload, 'primaryImageTag') || '',
            parentPrimaryImageTag: readValue(payload, 'parentPrimaryImageTag') || '',
            parentPrimaryImageItemId: readValue(payload, 'parentPrimaryImageItemId') || ''
        };
    }

    function normalizeSession(session) {
        var item = readValue(session, 'NowPlayingItem');
        if (!item) {
            return null;
        }

        return normalizeNowPlaying({
            sessionId: readValue(session, 'Id'),
            itemId: readValue(item, 'Id'),
            itemType: readValue(item, 'Type'),
            name: readValue(item, 'Name'),
            seriesId: readValue(item, 'SeriesId'),
            seriesName: readValue(item, 'SeriesName'),
            seasonId: readValue(item, 'SeasonId'),
            seasonName: readValue(item, 'SeasonName'),
            indexNumber: readValue(item, 'IndexNumber'),
            parentIndexNumber: readValue(item, 'ParentIndexNumber'),
            runTimeTicks: readValue(item, 'RunTimeTicks'),
            positionTicks: readValue(session, 'PlayState') ? readValue(readValue(session, 'PlayState'), 'PositionTicks') : null,
            isPaused: readValue(session, 'PlayState') ? readValue(readValue(session, 'PlayState'), 'IsPaused') : false,
            productionYear: readValue(item, 'ProductionYear'),
            communityRating: readValue(item, 'CommunityRating'),
            overview: readValue(item, 'Overview'),
            primaryImageTag: readValue(readValue(item, 'ImageTags'), 'Primary'),
            parentPrimaryImageTag: readValue(item, 'ParentPrimaryImageTag'),
            parentPrimaryImageItemId: readValue(item, 'ParentPrimaryImageItemId')
        });
    }

    function refreshNowPlaying(force) {
        if (!isLoggedIn()) {
            return Promise.resolve(null);
        }

        var now = Date.now();
        if (!force && state.nowPlayingPromise) {
            return state.nowPlayingPromise;
        }
        if (!force && now - state.lastNowPlayingCheck < 2500) {
            return Promise.resolve(state.item);
        }
        state.lastNowPlayingCheck = now;

        var url = apiUrl('NowPlaying', deviceId() ? { deviceId: deviceId() } : null);
        state.nowPlayingPromise = getJson(url)
            .then(function (payload) {
                var current = normalizeNowPlaying(payload);
                if (current) {
                    return current;
                }
                return refreshFromSessions();
            })
            .catch(function () {
                return refreshFromSessions();
            })
            .then(function (current) {
                state.nowPlayingPromise = null;
                applyNowPlaying(current);
                return current;
            });

        return state.nowPlayingPromise;
    }

    function refreshFromSessions() {
        if (!window.ApiClient || typeof ApiClient.getSessions !== 'function') {
            return Promise.resolve(null);
        }

        var query = {};
        if (deviceId()) {
            query.DeviceId = deviceId();
        }

        return ApiClient.getSessions(query)
            .then(function (sessions) {
                var list = sessions || [];
                for (var index = 0; index < list.length; index += 1) {
                    var candidate = normalizeSession(list[index]);
                    if (candidate) {
                        return candidate;
                    }
                }
                return null;
            })
            .catch(function () {
                return null;
            });
    }

    function applyNowPlaying(current) {
        if (!current) {
            state.item = null;
            state.sessionId = null;
            return;
        }

        var changed = !state.item || state.item.itemId !== current.itemId;
        state.item = current;
        state.sessionId = current.sessionId || state.sessionId;

        if (changed) {
            resetForNewItem();
            loadCredits(current);
        }
    }

    function resetForNewItem() {
        state.activationPending = false;
        state.dismissed = false;
        state.creditsStart = null;
        state.creditsItemId = null;
        state.creditsSource = null;
        state.creditsAttempts = 0;
        state.creditsLastAttempt = 0;
        if (state.activated) {
            closeSuggestions('item-change', true);
        }
    }

    function loadCredits(item, force) {
        if (!item || !item.itemId || !isLoggedIn()) {
            return Promise.resolve(null);
        }

        if (!force && state.creditsItemId === item.itemId) {
            return Promise.resolve(state.creditsStart);
        }

        if (state.creditsPendingItemId === item.itemId) {
            return Promise.resolve(state.creditsStart);
        }

        state.creditsItemId = item.itemId;
        state.creditsPendingItemId = item.itemId;
        state.creditsAttempts += 1;
        state.creditsLastAttempt = Date.now();
        state.creditsStart = null;
        state.creditsSource = null;

        return getJson(apiUrl('Credits', { itemId: item.itemId }))
            .then(function (payload) {
                state.creditsPendingItemId = null;

                if (!state.item || state.item.itemId !== item.itemId) {
                    return null;
                }

                var seconds = Number(readValue(payload, 'startSeconds'));
                if (!isFinite(seconds) || seconds <= 0) {
                    var ticks = Number(readValue(payload, 'startTicks'));
                    seconds = isFinite(ticks) && ticks > 0 ? ticks / 10000000 : 0;
                }

                var runtime = item.runTimeTicks ? item.runTimeTicks / 10000000 : 0;
                if (seconds > 0 && (!runtime || seconds < runtime - 1)) {
                    state.creditsStart = seconds;
                    state.creditsSource = readValue(payload, 'source') || 'unknown';
                }

                return state.creditsStart;
            })
            .catch(function () {
                state.creditsPendingItemId = null;
                // Allow a retry on the next tick: the token or the session may
                // not be ready yet when playback starts.
                state.creditsItemId = null;
                return null;
            });
    }

    function retryCreditsIfNeeded() {
        if (!state.item || state.creditsStart || state.activated || state.dismissed) {
            return;
        }

        if (state.creditsAttempts >= 20) {
            return;
        }

        if (Date.now() - state.creditsLastAttempt < 15000) {
            return;
        }

        loadCredits(state.item, true);
    }

    function isPlayerActive(video) {
        if (!video || video.tagName !== 'VIDEO' || !document.contains(video)) {
            return false;
        }

        if (!video.currentSrc) {
            return false;
        }

        if (isFinite(video.readyState) && video.readyState < 2) {
            return false;
        }

        var rect = video.getBoundingClientRect();
        if (rect.width < 160 || rect.height < 90) {
            return false;
        }

        var style = window.getComputedStyle(video);
        if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') {
            return false;
        }

        return isPlaybackRoute();
    }

    function isPlaybackRoute() {
        if (document.fullscreenElement) {
            return true;
        }

        if (state.playerHost && document.contains(state.playerHost)) {
            var hostRect = state.playerHost.getBoundingClientRect();
            if (hostRect.width >= 320 && hostRect.height >= 180) {
                return true;
            }
        }

        var container = document.querySelector('.videoPlayerContainer');
        if (container) {
            var rect = container.getBoundingClientRect();
            if (rect.width >= 320 && rect.height >= 180) {
                return true;
            }
        }

        return !!(document.querySelector('.videoOsdBottom') && state.video);
    }

    function matchesCurrentItem(video, item) {
        if (!item || !item.runTimeTicks) {
            return true;
        }

        if (!video || !isFinite(video.duration) || video.duration <= 0) {
            return true;
        }

        var expected = item.runTimeTicks / 10000000;
        return Math.abs(video.duration - expected) <= Math.max(120, expected * 0.12);
    }

    function findPlayerHost(video) {
        if (!video) {
            return null;
        }

        var host = video.closest ? video.closest('.videoPlayerContainer') : null;
        if (host) {
            return host;
        }

        var node = video.parentElement;
        while (node && node !== document.body) {
            var style = window.getComputedStyle(node);
            if (style.position === 'fixed' || style.position === 'absolute') {
                return node;
            }
            node = node.parentElement;
        }

        return video.parentElement;
    }

    function rememberVideo(video) {
        if (!video || video.tagName !== 'VIDEO') {
            return;
        }

        if (state.video && state.video !== video && !state.activated) {
            state.video = null;
        }

        state.video = video;
        state.playerHost = findPlayerHost(video);

        if (!state.item) {
            refreshNowPlaying(true);
        }
    }

    function findActiveVideo() {
        var videos = document.querySelectorAll('video');
        var fallback = null;

        for (var index = 0; index < videos.length; index += 1) {
            var video = videos[index];
            if (!fallback) {
                fallback = video;
            }
            if (!video.paused && !video.ended) {
                rememberVideo(video);
                return video;
            }
        }

        if (fallback) {
            rememberVideo(fallback);
            return fallback;
        }

        if (state.video && !document.contains(state.video)) {
            state.video = null;
            state.playerHost = null;
        }

        return state.video;
    }

    function videoRemainingSeconds(video, item) {
        var duration = 0;
        if (video && isFinite(video.duration) && video.duration > 0) {
            duration = video.duration;
        } else if (item && item.runTimeTicks) {
            duration = item.runTimeTicks / 10000000;
        }

        var position = 0;
        if (video && isFinite(video.currentTime) && video.currentTime > 0) {
            position = video.currentTime;
        } else if (item && item.positionTicks) {
            position = item.positionTicks / 10000000;
        }

        if (duration <= 0) {
            return null;
        }

        return Math.max(0, duration - position);
    }

    function checkTrigger(video, ended) {
        if (!state.config.enabled || state.activated || state.activationPending || state.dismissed) {
            return;
        }

        var item = state.item;
        if (!item || !item.itemId) {
            return;
        }

        if (ended !== true && (!video || video.paused)) {
            return;
        }

        if (!isPlayerActive(video) || !matchesCurrentItem(video, item)) {
            return;
        }

        var duration = video && isFinite(video.duration) && video.duration > 0
            ? video.duration
            : (item.runTimeTicks ? item.runTimeTicks / 10000000 : 0);
        var position = video && isFinite(video.currentTime) ? video.currentTime : 0;
        var threshold = Math.max(1, state.config.triggerSecondsBeforeEnd);
        var creditsStart = state.creditsStart;
        var nearEnd;

        if (creditsStart && duration > 0 && creditsStart > 0 && creditsStart < duration - 1) {
            nearEnd = ended === true || position >= creditsStart - 0.5;
        } else {
            var remaining = duration > 0 ? Math.max(0, duration - position) : videoRemainingSeconds(video, item);
            nearEnd = ended === true || (remaining !== null && remaining <= threshold);
        }

        if (!nearEnd) {
            return;
        }

        state.activationPending = true;

        var allowed;
        if (item.itemType === 'Movie') {
            allowed = Promise.resolve(true);
        } else if (item.itemType === 'Episode') {
            if (!state.config.showOnSeriesFinaleOnly) {
                allowed = Promise.resolve(true);
            } else {
                allowed = isFinaleEpisode(item);
            }
        } else {
            allowed = Promise.resolve(false);
        }

        allowed.then(function (canShow) {
            state.activationPending = false;
            if (canShow && state.config.enabled && !state.activated && !state.dismissed) {
                if (!isPlayerActive(video)) {
                    return;
                }
                activate(item, video);
            }
        }).catch(function () {
            state.activationPending = false;
        });
    }

    function isFinaleEpisode(item) {
        if (!item || !item.seriesId) {
            return Promise.resolve(false);
        }

        var cached = state.finaleCache[item.seriesId];
        if (cached && Date.now() - cached.timestamp < 10 * 60 * 1000) {
            return Promise.resolve(cached.isFinale);
        }

        var userId = currentUserId();
        var url = ApiClient.getUrl('Shows/' + item.seriesId + '/Episodes', userId ? { userId: userId } : null);

        return getJson(url).then(function (result) {
            var episodes = (result && result.Items) || [];
            var regular = episodes.filter(function (episode) {
                return (episode.ParentIndexNumber || 0) > 0;
            });
            if (!regular.length) {
                regular = episodes;
            }

            if (!regular.length) {
                return false;
            }

            var finalSeason = 0;
            regular.forEach(function (episode) {
                finalSeason = Math.max(finalSeason, episode.ParentIndexNumber || 0);
            });

            var seasonEpisodes = regular.filter(function (episode) {
                return (episode.ParentIndexNumber || 0) === finalSeason;
            }).sort(function (left, right) {
                var leftIndex = left.IndexNumber || 0;
                var rightIndex = right.IndexNumber || 0;
                if (leftIndex !== rightIndex) {
                    return leftIndex - rightIndex;
                }
                return (left.IndexNumberEnd || 0) - (right.IndexNumberEnd || 0);
            });

            var lastEpisode = seasonEpisodes[seasonEpisodes.length - 1];
            var isFinale = !!lastEpisode && lastEpisode.Id === item.itemId;
            state.finaleCache[item.seriesId] = { timestamp: Date.now(), isFinale: isFinale };
            return isFinale;
        }).catch(function () {
            return getJson(apiUrl('Finale', { itemId: item.itemId }))
                .then(function (result) {
                    var isFinale = !!(result && result.isFinale);
                    state.finaleCache[item.seriesId] = { timestamp: Date.now(), isFinale: isFinale };
                    return isFinale;
                })
                .catch(function () {
                    return false;
                });
        });
    }

    function activate(item, video) {
        if (state.activated || !state.config.enabled) {
            return;
        }

        state.activated = true;
        state.activationPending = false;
        state.video = video || state.video;
        state.playerHost = findPlayerHost(state.video);

        var fullscreenElement = document.fullscreenElement;
        var isTouch = ('ontouchstart' in window) || (navigator.maxTouchPoints > 0);
        state.wasFullscreen = !!fullscreenElement;
        state.keepFullscreen = !!(fullscreenElement
            && isTouch
            && fullscreenElement.tagName !== 'VIDEO');

        if (fullscreenElement && document.exitFullscreen && !state.keepFullscreen) {
            try {
                var exitResult = document.exitFullscreen();
                if (exitResult && exitResult.catch) {
                    exitResult.catch(function () {});
                }
            } catch (error) {
                // Some WebViews do not expose the Fullscreen API.
            }
        }

        document.body.classList.add('pps-active');
        if (state.keepFullscreen) {
            document.body.classList.add('pps-fullscreen');
        }
        if (state.playerHost) {
            state.playerHost.classList.add('pps-player-host');
        }

        buildOverlay(item);
        applyTheme();
        reportPanel(true);

        loadSuggestions(item).then(function (items) {
            state.suggestions = items;
            renderSuggestions(items);
        }).catch(function () {
            renderSuggestions([]);
        });
    }

    function closeSuggestions(reason, silent) {
        var wasActive = state.activated;

        if (state.overlay && state.overlay.parentNode) {
            state.overlay.parentNode.removeChild(state.overlay);
        }
        state.overlay = null;
        state.activated = false;
        state.activationPending = false;
        state.suggestions = [];

        document.body.classList.remove('pps-active');
        document.body.classList.remove('pps-fullscreen');

        if (wasActive) {
            reportPanel(false);
        }

        if (state.playerHost) {
            state.playerHost.classList.remove('pps-player-host');
        }

        if (reason !== 'item-change') {
            state.dismissed = true;
        }

        if (!state.keepFullscreen
            && state.wasFullscreen
            && !silent
            && state.playerHost
            && state.playerHost.requestFullscreen) {
            try {
                var requestResult = state.playerHost.requestFullscreen();
                if (requestResult && requestResult.catch) {
                    requestResult.catch(function () {});
                }
            } catch (error) {
                // Fullscreen can be blocked when there is no user gesture.
            }
        }
        state.wasFullscreen = false;
        state.keepFullscreen = false;
    }

    function overlayHost() {
        if (state.keepFullscreen
            && document.fullscreenElement
            && document.fullscreenElement.tagName !== 'VIDEO') {
            return document.fullscreenElement;
        }

        return document.body;
    }

    function buildOverlay(item) {
        if (state.overlay && state.overlay.parentNode) {
            state.overlay.parentNode.removeChild(state.overlay);
        }

        var overlay = document.createElement('div');
        overlay.className = 'pps-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Sugerencias después de la reproducción');

        var panel = document.createElement('section');
        panel.className = 'pps-panel';

        var header = document.createElement('header');
        header.className = 'pps-header';

        var headerText = document.createElement('div');
        headerText.className = 'pps-header-text';

        var eyebrow = document.createElement('p');
        eyebrow.className = 'pps-eyebrow';
        eyebrow.textContent = item.itemType === 'Episode' ? 'Serie finalizada' : 'A continuación';

        var title = document.createElement('h2');
        title.className = 'pps-title';
        title.textContent = '¿Qué quieres ver ahora?';

        var subtitle = document.createElement('p');
        subtitle.className = 'pps-subtitle';
        subtitle.textContent = item.name ? ('Terminaste «' + item.name + '»') : 'Elige tu próxima reproducción';

        headerText.appendChild(eyebrow);
        headerText.appendChild(title);
        headerText.appendChild(subtitle);

        var closeButton = document.createElement('button');
        closeButton.type = 'button';
        closeButton.className = 'pps-close';
        closeButton.setAttribute('data-action', 'close');
        closeButton.setAttribute('aria-label', 'Cerrar sugerencias');
        closeButton.innerHTML = '&times;';

        header.appendChild(headerText);
        header.appendChild(closeButton);

        var body = document.createElement('div');
        body.className = 'pps-body';
        body.innerHTML = '<div class="pps-skeleton-grid">' +
            '<span class="pps-skeleton"></span><span class="pps-skeleton"></span>' +
            '<span class="pps-skeleton"></span><span class="pps-skeleton"></span>' +
            '<span class="pps-skeleton"></span><span class="pps-skeleton"></span>' +
            '</div>';

        panel.appendChild(header);
        panel.appendChild(body);
        overlay.appendChild(panel);

        overlay.addEventListener('click', function (event) {
            if (!event.target || !event.target.closest) {
                return;
            }

            var actionElement = event.target.closest('[data-action]');
            if (actionElement) {
                var action = actionElement.getAttribute('data-action');
                var id = actionElement.getAttribute('data-id');
                var suggestion = findSuggestion(id);

                if (action === 'close') {
                    closeSuggestions('close');
                    return;
                }
                if (suggestion && action === 'play') {
                    playSuggestion(suggestion);
                    return;
                }
                if (suggestion && action === 'details') {
                    openDetails(suggestion, false);
                    return;
                }
            }

            if (event.target === overlay) {
                closeSuggestions('backdrop');
            }
        });

        overlayHost().appendChild(overlay);
        state.overlay = overlay;

        setTimeout(function () {
            if (state.overlay) {
                focusFirstSuggestion();
            }
        }, 50);
    }

    function focusFirstSuggestion() {
        if (!state.overlay) {
            return;
        }

        var target = state.overlay.querySelector('.pps-poster')
            || state.overlay.querySelector('.pps-btn-primary')
            || state.overlay.querySelector('.pps-close');
        if (target) {
            target.focus();
        }
    }

    function overlayFocusables() {
        if (!state.overlay) {
            return [];
        }

        var nodes = state.overlay.querySelectorAll('button, [href], [tabindex]:not([tabindex="-1"])');
        var result = [];

        for (var index = 0; index < nodes.length; index += 1) {
            var node = nodes[index];
            if (node.disabled) {
                continue;
            }
            var rect = node.getBoundingClientRect();
            if (rect.width < 2 && rect.height < 2) {
                continue;
            }
            result.push(node);
        }

        return result;
    }

    function moveOverlayFocus(direction) {
        var nodes = overlayFocusables();
        if (!nodes.length) {
            return false;
        }

        var active = document.activeElement;
        if (nodes.indexOf(active) === -1) {
            nodes[0].focus();
            return true;
        }

        var rect = active.getBoundingClientRect();
        var centerX = rect.left + (rect.width / 2);
        var centerY = rect.top + (rect.height / 2);
        var best = null;
        var bestScore = Infinity;

        for (var index = 0; index < nodes.length; index += 1) {
            var node = nodes[index];
            if (node === active) {
                continue;
            }

            var candidate = node.getBoundingClientRect();
            var x = candidate.left + (candidate.width / 2);
            var y = candidate.top + (candidate.height / 2);
            var dx = x - centerX;
            var dy = y - centerY;
            var inDirection;
            if (direction === 'left') {
                inDirection = dx < -4;
            } else if (direction === 'right') {
                inDirection = dx > 4;
            } else if (direction === 'up') {
                inDirection = dy < -4;
            } else {
                inDirection = dy > 4;
            }

            if (!inDirection) {
                continue;
            }

            var horizontal = direction === 'left' || direction === 'right';
            var primary = horizontal ? Math.abs(dx) : Math.abs(dy);
            var secondary = horizontal ? Math.abs(dy) : Math.abs(dx);
            var score = primary + (secondary * 2.2);

            if (score < bestScore) {
                bestScore = score;
                best = node;
            }
        }

        if (best) {
            best.focus();
            if (best.scrollIntoView) {
                best.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            }
            return true;
        }

        return false;
    }

    function onOverlayKeyDown(event) {
        if (!state.activated || !state.overlay) {
            return;
        }

        if (event.key === 'Escape' || event.key === 'GoBack' || event.key === 'BrowserBack') {
            event.preventDefault();
            closeSuggestions('escape');
            return;
        }

        var directions = {
            ArrowLeft: 'left',
            ArrowRight: 'right',
            ArrowUp: 'up',
            ArrowDown: 'down',
            Left: 'left',
            Right: 'right',
            Up: 'up',
            Down: 'down'
        };
        var direction = directions[event.key];
        if (direction && moveOverlayFocus(direction)) {
            event.preventDefault();
        }
    }

    function imageUrl(item) {
        if (!window.ApiClient || !item) {
            return '';
        }

        var imageId = item.id;
        var tag = item.primaryImageTag;

        if (!tag && item.parentPrimaryImageItemId) {
            imageId = item.parentPrimaryImageItemId;
            tag = item.parentPrimaryImageTag;
        }

        if (!tag && item.seriesId) {
            imageId = item.seriesId;
            tag = item.seriesPrimaryImageTag;
        }

        try {
            return ApiClient.getImageUrl(imageId, {
                type: 'Primary',
                maxWidth: 520,
                quality: 90,
                tag: tag || undefined
            });
        } catch (error) {
            return '';
        }
    }

    function formatRuntime(ticks) {
        if (!ticks) {
            return '';
        }
        var minutes = Math.round(ticks / 600000000);
        if (minutes < 60) {
            return minutes + ' min';
        }
        var hours = Math.floor(minutes / 60);
        var rest = minutes % 60;
        return hours + ' h' + (rest ? ' ' + rest + ' min' : '');
    }

    function itemMeta(item) {
        var parts = [];
        if (item.type === 'Episode') {
            var season = item.parentIndexNumber;
            var episode = item.indexNumber;
            if (season !== null && season !== undefined && episode !== null && episode !== undefined) {
                parts.push('T' + season + 'E' + episode);
            }
            if (item.seriesName) {
                parts.push(item.seriesName);
            }
        } else if (item.productionYear) {
            parts.push(String(item.productionYear));
        }

        var runtime = formatRuntime(item.runTimeTicks);
        if (runtime) {
            parts.push(runtime);
        }

        if (item.communityRating) {
            parts.push('★ ' + Number(item.communityRating).toFixed(1));
        }

        return parts.join(' · ');
    }

    function loadSuggestions(item) {
        var userId = currentUserId();
        var limit = state.config.maxSuggestions;

        if (!userId || !item || !item.itemId) {
            return Promise.resolve([]);
        }

        var fields = 'PrimaryImageAspectRatio,ProductionYear,CommunityRating,RunTimeTicks,Overview';
        var similarUrl = ApiClient.getUrl('Items/' + item.itemId + '/Similar', {
            userId: userId,
            limit: limit,
            fields: fields
        });

        return getJson(similarUrl).then(function (result) {
            var items = filterSuggestions((result && result.Items) || [], item);
            if (items.length) {
                return items.slice(0, limit);
            }

            var sameType = item.itemType === 'Episode' ? 'Episode' : item.itemType;
            return loadRecent(userId, sameType, limit, item).then(function (recent) {
                if (recent.length) {
                    return recent;
                }
                return loadRecent(userId, 'Movie,Series', limit, item);
            });
        }).catch(function () {
            return loadRecent(userId, item.itemType === 'Episode' ? 'Episode' : item.itemType, limit, item)
                .then(function (recent) {
                    if (recent.length) {
                        return recent;
                    }
                    return loadRecent(userId, 'Movie,Series', limit, item);
                });
        });
    }

    function loadRecent(userId, includeItemTypes, limit, currentItem) {
        if (!includeItemTypes) {
            return Promise.resolve([]);
        }

        return ApiClient.getItems(userId, {
            Recursive: true,
            IncludeItemTypes: includeItemTypes,
            SortBy: 'DateCreated',
            SortOrder: 'Descending',
            Limit: Math.max(limit * 2, 12),
            Fields: 'PrimaryImageAspectRatio,ProductionYear,CommunityRating,RunTimeTicks,Overview'
        }).then(function (result) {
            return filterSuggestions((result && result.Items) || [], currentItem).slice(0, limit);
        }).catch(function () {
            return [];
        });
    }

    function filterSuggestions(items, currentItem) {
        var allowed = {
            Movie: true,
            Series: true,
            Episode: true,
            MusicVideo: true,
            Video: true
        };
        var seen = {};
        var result = [];

        items.forEach(function (item) {
            if (!item || !item.Id || item.Id === currentItem.itemId) {
                return;
            }
            if (currentItem.seriesId && item.SeriesId === currentItem.seriesId) {
                return;
            }
            if (item.Type && !allowed[item.Type]) {
                return;
            }
            if (seen[item.Id]) {
                return;
            }
            seen[item.Id] = true;
            result.push(item);
        });

        return result;
    }

    function renderSuggestions(items) {
        if (!state.overlay) {
            return;
        }

        var body = state.overlay.querySelector('.pps-body');
        if (!body) {
            return;
        }

        if (!items.length) {
            body.innerHTML = '<div class="pps-empty">' +
                '<p>No encontramos sugerencias para este título.</p>' +
                '<button type="button" class="pps-btn pps-btn-primary" data-action="close">Volver al reproductor</button>' +
                '</div>';
            var emptyButton = body.querySelector('.pps-btn');
            if (emptyButton) {
                emptyButton.focus();
            }
            return;
        }

        var grid = document.createElement('div');
        grid.className = 'pps-grid';

        items.forEach(function (item) {
            var card = document.createElement('article');
            card.className = 'pps-card';

            var poster = document.createElement('button');
            poster.type = 'button';
            poster.className = 'pps-poster';
            poster.setAttribute('data-action', 'play');
            poster.setAttribute('data-id', item.Id);
            poster.setAttribute('aria-label', 'Reproducir ' + (item.Name || 'elemento'));

            var image = imageUrl({
                id: item.Id,
                primaryImageTag: readValue(item, 'ImageTags') ? readValue(readValue(item, 'ImageTags'), 'Primary') : '',
                parentPrimaryImageItemId: readValue(item, 'ParentPrimaryImageItemId'),
                parentPrimaryImageTag: readValue(item, 'ParentPrimaryImageTag'),
                seriesId: readValue(item, 'SeriesId'),
                seriesPrimaryImageTag: readValue(item, 'SeriesPrimaryImageTag')
            });

            if (image) {
                var img = document.createElement('img');
                img.src = image;
                img.alt = '';
                img.loading = 'lazy';
                poster.appendChild(img);
            } else {
                poster.classList.add('pps-poster-empty');
            }

            var playBadge = document.createElement('span');
            playBadge.className = 'pps-play-badge';
            playBadge.textContent = '▶';
            poster.appendChild(playBadge);

            var info = document.createElement('div');
            info.className = 'pps-card-info';

            var title = document.createElement('h3');
            title.className = 'pps-card-title';
            title.textContent = item.Name || 'Sin título';

            var meta = document.createElement('p');
            meta.className = 'pps-card-meta';
            meta.textContent = itemMeta({
                type: item.Type,
                parentIndexNumber: readValue(item, 'ParentIndexNumber'),
                indexNumber: readValue(item, 'IndexNumber'),
                seriesName: readValue(item, 'SeriesName'),
                productionYear: readValue(item, 'ProductionYear'),
                runTimeTicks: readValue(item, 'RunTimeTicks'),
                communityRating: readValue(item, 'CommunityRating')
            });

            var actions = document.createElement('div');
            actions.className = 'pps-card-actions';

            var playButton = document.createElement('button');
            playButton.type = 'button';
            playButton.className = 'pps-btn pps-btn-primary';
            playButton.setAttribute('data-action', 'play');
            playButton.setAttribute('data-id', item.Id);
            playButton.textContent = 'Reproducir';

            var detailsButton = document.createElement('button');
            detailsButton.type = 'button';
            detailsButton.className = 'pps-btn';
            detailsButton.setAttribute('data-action', 'details');
            detailsButton.setAttribute('data-id', item.Id);
            detailsButton.textContent = 'Detalles';

            actions.appendChild(playButton);
            actions.appendChild(detailsButton);

            info.appendChild(title);
            if (meta.textContent) {
                info.appendChild(meta);
            }
            info.appendChild(actions);

            card.appendChild(poster);
            card.appendChild(info);
            grid.appendChild(card);
        });

        body.innerHTML = '';
        body.appendChild(grid);
        focusFirstSuggestion();
    }

    function findSuggestion(id) {
        if (!id) {
            return null;
        }

        for (var index = 0; index < state.suggestions.length; index += 1) {
            var entry = state.suggestions[index];
            if (readValue(entry, 'Id') === id) {
                return entry;
            }
        }

        return null;
    }

    function playSuggestion(item) {
        var sessionId = state.sessionId;
        var itemId = readValue(item, 'Id');
        closeSuggestions('play');

        if (!itemId) {
            return;
        }

        if (!sessionId || !window.ApiClient || typeof ApiClient.ajax !== 'function') {
            openDetails(item, true);
            return;
        }

        var url = ApiClient.getUrl('Sessions/' + sessionId + '/Playing', {
            PlayCommand: 'PlayNow',
            ItemIds: itemId
        });

        ApiClient.ajax({
            type: 'POST',
            url: url,
            contentType: 'application/json'
        }).catch(function () {
            openDetails(item, true);
        });
    }

    function openDetails(item, autoplay) {
        var itemId = readValue(item, 'Id');
        if (!itemId) {
            return;
        }

        closeSuggestions('details');

        var serverId = '';
        try {
            serverId = ApiClient.serverId ? ApiClient.serverId() : '';
        } catch (error) {
            serverId = '';
        }

        var hash = '#/details?id=' + encodeURIComponent(itemId);
        if (serverId) {
            hash += '&serverId=' + encodeURIComponent(serverId);
        }
        window.location.hash = hash;

        if (autoplay) {
            autoClickPlayButton();
        }
    }

    function autoClickPlayButton() {
        var attempts = 0;
        var timer = window.setInterval(function () {
            attempts += 1;
            var button = document.querySelector('.btnPlay:not(.hide), .btnResume:not(.hide)');
            if (button) {
                window.clearInterval(timer);
                button.click();
                return;
            }
            if (attempts > 48) {
                window.clearInterval(timer);
            }
        }, 250);
    }

    function parseColor(value) {
        if (!value) {
            return null;
        }

        var text = String(value).trim();
        if (!text || text === 'transparent' || text === 'initial' || text === 'inherit') {
            return null;
        }

        var hex = text.match(/^#([0-9a-f]{3,8})$/i);
        if (hex) {
            var digits = hex[1];
            if (digits.length === 3) {
                digits = digits.split('').map(function (part) { return part + part; }).join('');
            }
            if (digits.length === 6 || digits.length === 8) {
                return {
                    r: parseInt(digits.substring(0, 2), 16),
                    g: parseInt(digits.substring(2, 4), 16),
                    b: parseInt(digits.substring(4, 6), 16),
                    a: digits.length === 8 ? parseInt(digits.substring(6, 8), 16) / 255 : 1
                };
            }
        }

        var match = text.match(/rgba?\(([^)]+)\)/i);
        if (!match) {
            return null;
        }

        var parts = match[1].split(/[,\s/]+/).filter(function (part) { return part; }).map(parseFloat);
        if (parts.length < 3 || parts.some(function (part) { return isNaN(part); })) {
            return null;
        }

        var alpha = parts.length > 3 ? parts[3] : 1;
        if (alpha <= 0.04) {
            return null;
        }

        return {
            r: Math.max(0, Math.min(255, parts[0])),
            g: Math.max(0, Math.min(255, parts[1])),
            b: Math.max(0, Math.min(255, parts[2])),
            a: Math.max(0, Math.min(1, alpha))
        };
    }

    function toCss(color) {
        if (!color) {
            return '';
        }
        if (color.a < 0.999) {
            return 'rgba(' + color.r + ', ' + color.g + ', ' + color.b + ', ' + color.a.toFixed(3) + ')';
        }
        return 'rgb(' + color.r + ', ' + color.g + ', ' + color.b + ')';
    }

    function mix(left, right, amount) {
        return {
            r: Math.round(left.r + (right.r - left.r) * amount),
            g: Math.round(left.g + (right.g - left.g) * amount),
            b: Math.round(left.b + (right.b - left.b) * amount),
            a: 1
        };
    }

    function luminance(color) {
        function channel(value) {
            var normalized = value / 255;
            return normalized <= 0.03928 ? normalized / 12.92 : Math.pow((normalized + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * channel(color.r) + 0.7152 * channel(color.g) + 0.0722 * channel(color.b);
    }

    function sameColor(left, right) {
        return !!left && !!right && Math.abs(left.r - right.r) < 8
            && Math.abs(left.g - right.g) < 8
            && Math.abs(left.b - right.b) < 8;
    }

    function readCssVariable(names) {
        var nodes = [document.documentElement, document.body];
        for (var nameIndex = 0; nameIndex < names.length; nameIndex += 1) {
            for (var nodeIndex = 0; nodeIndex < nodes.length; nodeIndex += 1) {
                if (!nodes[nodeIndex]) {
                    continue;
                }
                var raw = window.getComputedStyle(nodes[nodeIndex]).getPropertyValue(names[nameIndex]).trim();
                var parsed = parseColor(raw);
                if (parsed) {
                    return parsed;
                }
            }
        }
        return null;
    }

    function probeColor(selectors, property) {
        for (var index = 0; index < selectors.length; index += 1) {
            var element = document.querySelector(selectors[index]);
            if (!element) {
                continue;
            }
            var parsed = parseColor(window.getComputedStyle(element)[property]);
            if (parsed) {
                return parsed;
            }
        }
        return null;
    }

    function applyTheme() {
        if (!document.body) {
            return;
        }

        var bg = readCssVariable([
            '--background-color', '--body-background', '--theme-background',
            '--palette-background-default', '--mui-palette-background-default'
        ]) || probeColor([
            'body', 'html', '.backgroundContainer', '.mainAnimatedPage',
            '.MuiPaper-root', '.videoPlayerContainer'
        ], 'backgroundColor') || { r: 16, g: 16, b: 16, a: 1 };

        var text = readCssVariable([
            '--text-color', '--body-text-color', '--theme-text', '--primary-text-color',
            '--palette-text-primary', '--mui-palette-text-primary'
        ]) || probeColor([
            'body', '.cardText', '.MuiTypography-root', 'h1', '.detailPagePrimaryContainer'
        ], 'color') || { r: 255, g: 255, b: 255, a: 1 };

        var accent = readCssVariable([
            '--primary-accent-color', '--accent-color', '--accent', '--theme-primary-color',
            '--jellyfin-accent', '--palette-primary-main', '--mui-palette-primary-main',
            '--button-primary-background', '--btn-primary-bg', '--primary-color'
        ]) || probeColor([
            '.MuiButton-containedPrimary', '.MuiButton-contained', 'button.raised',
            '.raised.button-submit', '[is="emby-button"].raised', '.btnPrimary',
            '.emby-button.raised', '.navMenuOption-selected', '.emby-tabs-slider',
            '.mdl-tabs__tab-bar .is-active', '.videoOsdBottom .btnSubmit'
        ], 'backgroundColor');

        var surface = readCssVariable([
            '--surface-color', '--card-background', '--paper-background', '--theme-surface',
            '--palette-background-paper', '--mui-palette-background-paper'
        ]) || probeColor([
            '.cardBox', '.card', '.paperList', '.dialog', '.MuiPaper-root',
            '.formDialogFooter', '.detailPageWrapperContainer'
        ], 'backgroundColor');

        var border = readCssVariable([
            '--border-color', '--divider-color', '--theme-border', '--palette-divider',
            '--mui-palette-divider'
        ]) || probeColor([
            '.cardBox', '.MuiPaper-root', '.inputContainer', '.paperList'
        ], 'borderTopColor');

        if (!accent || sameColor(accent, bg) || sameColor(accent, text)) {
            accent = { r: 0, g: 164, b: 220, a: 1 };
        }

        if (sameColor(text, bg) || Math.abs(luminance(text) - luminance(bg)) < 0.18) {
            text = luminance(bg) > 0.5 ? { r: 20, g: 20, b: 20, a: 1 } : { r: 255, g: 255, b: 255, a: 1 };
        }

        if (!surface || sameColor(surface, bg)) {
            surface = mix(bg, text, 0.08);
        }

        if (!border) {
            border = mix(text, bg, 0.2);
        }

        var muted = mix(text, bg, 0.38);
        var accentText = luminance(accent) > 0.52
            ? { r: 10, g: 12, b: 14, a: 1 }
            : { r: 255, g: 255, b: 255, a: 1 };

        var root = document.documentElement.style;
        root.setProperty('--pps-bg', toCss(bg));
        root.setProperty('--pps-surface', toCss(surface));
        root.setProperty('--pps-text', toCss(text));
        root.setProperty('--pps-muted', toCss(muted));
        root.setProperty('--pps-accent', toCss(accent));
        root.setProperty('--pps-accent-text', toCss(accentText));
        root.setProperty('--pps-border', toCss(border));
        root.setProperty('--pps-shadow', '0 18px 60px rgba(0, 0, 0, ' + (luminance(bg) > 0.5 ? '0.18' : '0.55') + ')');
    }

    function scheduleTheme() {
        if (state.themeTimer) {
            window.clearTimeout(state.themeTimer);
        }
        state.themeTimer = window.setTimeout(function () {
            state.themeTimer = null;
            applyTheme();
        }, 220);
    }

    function onDocumentPlay(event) {
        var video = event.target;
        if (!video || video.tagName !== 'VIDEO') {
            return;
        }

        if (video.currentTime < 5) {
            state.dismissed = false;
        }
        rememberVideo(video);
        refreshNowPlaying(true);
    }

    function onDocumentTimeUpdate(event) {
        var video = event.target;
        if (!video || video.tagName !== 'VIDEO' || video !== state.video) {
            return;
        }
        checkTrigger(video, false);
    }

    function onDocumentEnded(event) {
        var video = event.target;
        if (!video || video.tagName !== 'VIDEO') {
            return;
        }
        rememberVideo(video);
        checkTrigger(video, true);
    }

    function onDocumentEmptied(event) {
        if (event.target === state.video && !state.activated) {
            state.video = null;
        }
    }

    function tick() {
        if (Date.now() - state.lastConfigCheck > 60000) {
            fetchConfig();
        }

        if (!isLoggedIn()) {
            return;
        }

        retryCreditsIfNeeded();

        if (state.activated && (!state.video || !document.contains(state.video))) {
            closeSuggestions('video-gone');
            state.item = null;
            state.sessionId = null;
            return;
        }

        var video = state.video;
        if (!video || !document.contains(video)) {
            video = findActiveVideo();
        }

        if (video && !video.paused && !video.ended) {
            refreshNowPlaying(false);
            checkTrigger(video, false);
        } else if (!state.item) {
            refreshNowPlaying(false);
        }
    }

    function handleMutations(mutations) {
        var themeChanged = false;

        for (var index = 0; index < mutations.length; index += 1) {
            var mutation = mutations[index];
            if (mutation.target && mutation.target.closest && mutation.target.closest('.pps-overlay')) {
                continue;
            }

            if (mutation.type === 'childList') {
                if (mutation.addedNodes && mutation.addedNodes.length) {
                    for (var nodeIndex = 0; nodeIndex < mutation.addedNodes.length; nodeIndex += 1) {
                        var node = mutation.addedNodes[nodeIndex];
                        if (node.nodeType === 1 && (node.tagName === 'VIDEO' || node.querySelector && node.querySelector('video'))) {
                            findActiveVideo();
                            break;
                        }
                    }
                }
                themeChanged = true;
            } else if (mutation.type === 'attributes') {
                themeChanged = true;
            }
        }

        if (themeChanged) {
            scheduleTheme();
        }
    }

    function start() {
        document.addEventListener('play', onDocumentPlay, true);
        document.addEventListener('timeupdate', onDocumentTimeUpdate, true);
        document.addEventListener('ended', onDocumentEnded, true);
        document.addEventListener('emptied', onDocumentEmptied, true);
        document.addEventListener('keydown', onOverlayKeyDown);
        window.addEventListener('hashchange', function () {
            findActiveVideo();
            refreshNowPlaying(true);
            scheduleTheme();
        });
        window.addEventListener('pageshow', scheduleTheme);
        window.addEventListener('resize', scheduleTheme);
        window.addEventListener('fullscreenchange', function () {
            scheduleTheme();
            if (state.overlay) {
                var host = overlayHost();
                if (state.overlay.parentNode !== host) {
                    host.appendChild(state.overlay);
                }
            }
        });

        if (document.body) {
            new MutationObserver(handleMutations).observe(document.body, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['class', 'style', 'data-theme']
            });
        }

        window.setInterval(tick, 2000);
        findActiveVideo();
        refreshNowPlaying(false);
        applyTheme();
    }

    window.PostPlaySuggestions = {
        show: function (item) {
            activate(item || state.item, state.video);
        },
        close: function () {
            closeSuggestions('debug');
        },
        refreshTheme: applyTheme,
        refreshConfig: fetchConfig,
        getState: function () {
            return state;
        },
        version: VERSION
    };

    fetchConfig().then(start).catch(start);
})();
