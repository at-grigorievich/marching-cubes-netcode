// CrazyGames HTML5 SDK v3 — тонкий мост для CrazyGamesPlatform.cs (M5.2).
// SDK подключает шаблон страницы (crazygames-sdk-v3.js), ответы уходят в Unity через
// window.catacombsSend. Без SDK каждая функция отвечает так, чтобы игра не ждала вечно.

var CatacombsCrazy = {
  $Cg: {
    sdk: function () { return window.CrazyGames && window.CrazyGames.SDK ? window.CrazyGames.SDK : null; },
    ready: false,
    send: function (method, payload) {
      if (typeof window.catacombsSend === 'function') window.catacombsSend(method, payload);
    }
  },

  CgInit: function () {
    var sdk = Cg.sdk();
    if (!sdk) { Cg.send('OnSdkReady', ''); return; }

    sdk.init().then(function () {
      Cg.ready = true;

      var lang = '';
      var device = '';
      try {
        var info = sdk.user.systemInfo || {};
        lang = info.locale || info.countryCode || '';
        device = (info.device && info.device.type) || '';
      } catch (e) { }

      try { sdk.game.loadingStart(); } catch (e) { }

      Cg.send('OnSdkReady', lang + '|' + device);
    }).catch(function () { Cg.send('OnSdkReady', ''); });
  },

  CgLoading: function (loading) {
    var sdk = Cg.sdk();
    if (!sdk || !Cg.ready) return;

    try { if (loading) sdk.game.loadingStart(); else sdk.game.loadingStop(); } catch (e) { }
  },

  CgGameplay: function (playing) {
    var sdk = Cg.sdk();
    if (!sdk || !Cg.ready) return;

    try { if (playing) sdk.game.gameplayStart(); else sdk.game.gameplayStop(); } catch (e) { }
  },

  CgRequestAd: function (typePtr, tokenPtr) {
    var type = UTF8ToString(typePtr);
    var token = UTF8ToString(tokenPtr);
    var done = false;
    var finish = function (result) { if (done) return; done = true; Cg.send('OnAdDone', token + '|' + result); };

    var sdk = Cg.sdk();
    if (!sdk || !Cg.ready) { finish('failed'); return; }

    try {
      sdk.ad.requestAd(type, {
        adStarted: function () { },
        adFinished: function () { finish('completed'); },
        adError: function () { finish('failed'); }
      });
    } catch (e) { finish('failed'); }
  },

  CgCloudLoad: function () {
    var sdk = Cg.sdk();
    var value = '';

    try { if (sdk && Cg.ready && sdk.data) value = sdk.data.getItem('profile') || ''; } catch (e) { }

    Cg.send('OnCloudLoaded', value);
  },

  CgCloudSave: function (jsonPtr) {
    var json = UTF8ToString(jsonPtr);
    var sdk = Cg.sdk();

    try { if (sdk && Cg.ready && sdk.data) sdk.data.setItem('profile', json); } catch (e) { }
  },

  CgHappytime: function () {
    var sdk = Cg.sdk();
    try { if (sdk && Cg.ready) sdk.game.happytime(); } catch (e) { }
  }
};

autoAddDeps(CatacombsCrazy, '$Cg');
mergeInto(LibraryManager.library, CatacombsCrazy);
