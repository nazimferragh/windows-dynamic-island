// Ad blocking and skipping for the island's own YouTube panel (Search & play).
// Runs in the page before YouTube's scripts. Four layers:
//  1. strip the ad schedule out of the player data (most ads never start),
//  2. hide ad boxes and the "ad blockers aren't allowed" popup,
//  3. if an in-video ad still plays: mute it, press Skip as soon as it appears, jump to its end,
//     then restore the sound and speed,
//  4. (in C#) requests to ad servers are refused before they load.
(() => {
  if (window.__islandAdBlock) return;
  window.__islandAdBlock = true;

  // ---- 1. player data without ads
  const AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
  const prune = (data) => {
    try {
      if (data && typeof data === 'object') {
        for (const key of AD_KEYS) if (key in data) delete data[key];
        if (data.playerResponse) prune(data.playerResponse);
        if (Array.isArray(data)) data.forEach(prune);
      }
    } catch (e) { /* never break the page */ }
    return data;
  };

  const parse = JSON.parse;
  JSON.parse = function () {
    return prune(parse.apply(this, arguments));
  };

  const readJson = Response.prototype.json;
  Response.prototype.json = function () {
    return readJson.apply(this, arguments).then(prune);
  };

  let initialPlayer;
  try {
    Object.defineProperty(window, 'ytInitialPlayerResponse', {
      configurable: true,
      get: () => initialPlayer,
      set: (value) => { initialPlayer = prune(value); },
    });
  } catch (e) { /* already defined */ }

  // ---- 2. ad boxes
  const CSS = [
    'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer', 'ytd-promoted-sparkles-web-renderer',
    'ytd-display-ad-renderer', 'ytd-companion-slot-renderer', 'ytd-action-companion-ad-renderer',
    'ytd-banner-promo-renderer', 'ytd-statement-banner-renderer', 'ytd-player-legacy-desktop-watch-ads-renderer',
    'ytd-promoted-video-renderer', 'ytd-search-pyv-renderer', '#player-ads', '#masthead-ad',
    '.ytp-ad-overlay-container', '.ytp-ad-message-container', '.ytp-featured-product',
    'tp-yt-paper-dialog:has(ytd-enforcement-message-view-model)', 'ytd-enforcement-message-view-model',
  ].join(',') + '{display:none!important}';

  const addStyle = () => {
    if (!document.documentElement || document.getElementById('island-adblock')) return;
    const style = document.createElement('style');
    style.id = 'island-adblock';
    style.textContent = CSS;
    document.documentElement.appendChild(style);
  };

  // ---- 3. skip what slips through
  let saved = null; // the user's sound and speed, while an ad is being skipped

  const tick = () => {
    addStyle();
    const player = document.querySelector('#movie_player');
    const video = document.querySelector('#movie_player video, video.html5-main-video');
    if (!player || !video) return;

    const adPlaying = player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting');
    if (adPlaying) {
      if (!saved) saved = { muted: video.muted, rate: video.playbackRate };
      video.muted = true;
      const skip = document.querySelector(
        '.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-container button');
      if (skip) skip.click();
      if (isFinite(video.duration) && video.duration > 0 && video.currentTime < video.duration - 0.2) {
        video.currentTime = video.duration - 0.1;
      }
      video.playbackRate = 16;
    } else if (saved) {
      video.muted = saved.muted;
      video.playbackRate = saved.rate || 1;
      saved = null;
    }

    // The "ad blockers aren't allowed" popup pauses the video: close it and keep playing.
    const nag = document.querySelector('ytd-enforcement-message-view-model');
    if (nag) {
      const close = nag.querySelector('#dismiss-button button, button[aria-label]');
      if (close) close.click();
      if (video.paused) video.play().catch(() => {});
    }
  };

  setInterval(tick, 200);
})();
