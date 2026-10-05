// Yandex Games SDK v2 — тонкий мост для YandexPlatform.cs (M5.2).
// SDK подключает шаблон страницы (/sdk.js), ответы уходят в Unity через window.catacombsSend
// (его заводит тот же шаблон: SendMessage объекту PlatformBridge, с очередью до старта движка).
// Без SDK (локальный запуск) каждая функция отвечает так, чтобы игра не ждала вечно.

var CatacombsYandex = {
  $Ya: {
    sdk: null,
    player: null,
    send: function (method, payload) {
      if (typeof window.catacombsSend === 'function') window.catacombsSend(method, payload);
    }
  },

  YaInit: function () {
    if (typeof YaGames === 'undefined') { Ya.send('OnSdkReady', ''); return; }

    YaGames.init().then(function (sdk) {
      Ya.sdk = sdk;

      try {
        sdk.on('game_api_pause', function () { Ya.send('OnPlatformPause', '1'); });
        sdk.on('game_api_resume', function () { Ya.send('OnPlatformPause', '0'); });
      } catch (e) { }

      var lang = '';
      var device = '';
      try { lang = sdk.environment.i18n.lang || ''; } catch (e) { }
      try { device = sdk.deviceInfo.type || ''; } catch (e) { }

      Ya.send('OnSdkReady', lang + '|' + device);
    }).catch(function () { Ya.send('OnSdkReady', ''); });
  },

  YaReady: function () {
    try { Ya.sdk.features.LoadingAPI.ready(); } catch (e) { }
  },

  YaGameplay: function (playing) {
    try {
      if (playing) Ya.sdk.features.GameplayAPI.start();
      else Ya.sdk.features.GameplayAPI.stop();
    } catch (e) { }
  },

  YaShowFullscreen: function (tokenPtr) {
    var token = UTF8ToString(tokenPtr);
    var done = false;
    var finish = function (result) { if (done) return; done = true; Ya.send('OnAdDone', token + '|' + result); };

    if (!Ya.sdk) { finish('failed'); return; }

    try {
      Ya.sdk.adv.showFullscreenAdv({
        callbacks: {
          onClose: function (wasShown) { finish(wasShown ? 'completed' : 'failed'); },
          onError: function () { finish('failed'); }
        }
      });
    } catch (e) { finish('failed'); }
  },

  YaShowRewarded: function (tokenPtr) {
    var token = UTF8ToString(tokenPtr);
    var done = false;
    var rewarded = false;
    var finish = function (result) { if (done) return; done = true; Ya.send('OnAdDone', token + '|' + result); };

    if (!Ya.sdk) { finish('failed'); return; }

    try {
      Ya.sdk.adv.showRewardedVideo({
        callbacks: {
          // Награда — по onRewarded: закрытие без досмотра награды не даёт (требование площадки).
          onRewarded: function () { rewarded = true; },
          onClose: function () { finish(rewarded ? 'completed' : 'skipped'); },
          onError: function () { finish('failed'); }
        }
      });
    } catch (e) { finish('failed'); }
  },

  YaCloudLoad: function () {
    if (!Ya.sdk) { Ya.send('OnCloudLoaded', ''); return; }

    Ya.sdk.getPlayer({ scopes: false }).then(function (player) {
      Ya.player = player;
      return player.getData(['profile']);
    }).then(function (data) {
      Ya.send('OnCloudLoaded', data && typeof data.profile === 'string' ? data.profile : '');
    }).catch(function () { Ya.send('OnCloudLoaded', ''); });
  },

  YaCloudSave: function (jsonPtr) {
    var json = UTF8ToString(jsonPtr);
    if (!Ya.player) return;

    try { Ya.player.setData({ profile: json }, true); } catch (e) { }
  },

  YaSubmitScore: function (boardPtr, value) {
    var board = UTF8ToString(boardPtr);
    if (!Ya.sdk) return;

    try {
      Ya.sdk.getLeaderboards().then(function (boards) { return boards.setLeaderboardScore(board, value); }).catch(function () { });
    } catch (e) { }
  }
};

autoAddDeps(CatacombsYandex, '$Ya');
mergeInto(LibraryManager.library, CatacombsYandex);
