/*
 * Copyright (C) 2014 The Android Open Source Project
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using Cirrious.CrossCore.Platform;
using Cirrious.MvvmCross.Binding.BindingContext;
using Cirrious.MvvmCross.Droid.Views;
using Com.Google.Android.Exoplayer;
using Com.Google.Android.Exoplayer.Audio;
using Com.Google.Android.Exoplayer.Drm;
using Com.Google.Android.Exoplayer.Text;
using Com.Google.Android.Exoplayer.Util;
using Java.Lang;
using Java.Net;
using MvvmCross.ExoPlayer.Droid.Player;
using MvvmCross.ExoPlayer.Models;
using MvvmCross.ExoPlayer.ViewModels;
using Exception = Java.Lang.Exception;
using Uri = Android.Net.Uri;

namespace MvvmCross.ExoPlayer.Droid
{
	/// <summary>
	/// An activity that plays media using <see cref="MvxVideoPlayer"/>.
	/// </summary>
	[Activity(
		Name = "mvvmcross.exoplayer.droid.MvxVideoPlayerActivity",
		ConfigurationChanges = ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard | ConfigChanges.Orientation | ConfigChanges.ScreenSize,
		LaunchMode = LaunchMode.SingleInstance
		)]
	public class MvxVideoPlayerActivity : MvxActivity<MvxVideoPlayerViewModel>,
		ISurfaceHolderCallback,
		MvxVideoPlayer.IListener,
		MvxVideoPlayer.ICaptionListener,
		MvxVideoPlayer.ID3MetadataListener,
		AudioCapabilitiesReceiver.IListener
	{
		private static readonly CookieManager DefaultCookieManager;

		static MvxVideoPlayerActivity()
		{
			DefaultCookieManager = new CookieManager();
			DefaultCookieManager.SetCookiePolicy(CookiePolicy.AcceptOriginalServer);
		}

		private MvxVideoPlayerEventLogger _eventLogger;
		private MediaController _mediaController;
		private View _shutterView;
		private AspectRatioFrameLayout _videoFrame;
		private SurfaceView _surfaceView;
		private SubtitleLayout _subtitleLayout;
		private ProgressBar _progress;

		private MvxVideoPlayer _player;
		private bool _playerNeedsPrepare;
		private long _playerPosition;

		private MvxVideoItem _item;

		private AudioCapabilitiesReceiver _audioCapabilitiesReceiver;
		
		public MvxVideoItem Item
		{
			get { return _item; }
			set
			{
				if (_item == value)
				{
					return;
				}

				_item = value;
				PreparePlayer(true);
			}
		}

		public bool LoadingItem
		{
			get { return ViewModel.LoadingItem; }
			set { _progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone; }
		}

		#region Activity lifecycle

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			SetContentView(Resource.Layout.activity_videoplayer);
			var root = FindViewById(Resource.Id.root);
			
			_shutterView = FindViewById(Resource.Id.shutter);

			_videoFrame = FindViewById<AspectRatioFrameLayout>(Resource.Id.video_frame);
			_surfaceView = FindViewById<SurfaceView>(Resource.Id.surface_view);
			_surfaceView.Holder.AddCallback(this);
			_subtitleLayout = FindViewById<SubtitleLayout>(Resource.Id.subtitles);
			_progress = FindViewById<ProgressBar>(Resource.Id.progress);

			_mediaController = new MediaController(this);
			_mediaController.SetAnchorView(root);

			var currentHandler = CookieHandler.Default;
			if (currentHandler != DefaultCookieManager)
			{
				CookieHandler.Default = DefaultCookieManager;
			}

			_audioCapabilitiesReceiver = new AudioCapabilitiesReceiver(this, this);
			_audioCapabilitiesReceiver.Register();
		}

		protected override void OnNewIntent(Intent intent)
		{
			ReleasePlayer();
			_playerPosition = 0;
			Intent = intent;
		}

		protected override void OnViewModelSet()
		{
			base.OnViewModelSet();

			var set = this.CreateBindingSet<MvxVideoPlayerActivity, MvxVideoPlayerViewModel>();
			set.Bind(this).For(t => t.Item).To(vm => vm.VideoItem);
			set.Bind(this).For(t => t.LoadingItem).To(vm => vm.LoadingItem);
			set.Apply();
		}

		protected override void OnResume()
		{
			base.OnResume();

			RegisterTouchAndClickEvents();

			ConfigureSubtitleView();
			if (_player == null)
			{
				if (Item != null && Item.Url != null)
				{
					PreparePlayer(true);
				}
			}
			else
			{
				_player.Backgrounded = false;
			}
		}

		private void RegisterTouchAndClickEvents(View root = null)
		{
			if (root == null)
			{
				root = FindViewById(Resource.Id.root);
			}

			if (root == null)
			{
				MvxTrace.Error("Could not find root view. Can't register Events!");
				return;
			}

			root.Touch += RootOnTouch;
			root.KeyPress += RootOnKeyPress;
		}

		private void UnregisterTouchAndClickEvents()
		{
			var root = FindViewById(Resource.Id.root);
			if (root == null)
			{
				MvxTrace.Warning("Could not find root-View. Cannot unregister touch and click events.");
				return;
			}

			root.Touch -= RootOnTouch;
			root.KeyPress -= RootOnKeyPress;
		}

		private void RootOnKeyPress(object sender, View.KeyEventArgs args)
		{
			var view = sender as View;
			if (view == null)
			{
				MvxTrace.Warning("Received OnKeyPress-Event for root without a valid sender (should be a View instance). Event possibly will be ignored.");
				return;
			}

			if (args?.Event == null)
			{
				MvxTrace.Error("Received OnKeyPress-Event for root without valid KeyEventArgs (should be a KeyEventsArgs instance with an event). Event will be ignored!");
				return;
			}

			var keyCode = args.KeyCode;
			if (keyCode == Keycode.Back || keyCode == Keycode.Escape
				|| keyCode == Keycode.Menu)
			{
				args.Handled = false;
			}
			else if (_mediaController != null)
			{
				_mediaController.DispatchKeyEvent(args.Event);
			}
			else
			{
				MvxTrace.Warning("Received OnKeyPress-Event for root but could not handle it.");
			}
		}

		private void RootOnTouch(object sender, View.TouchEventArgs args)
		{
			var view = sender as View;
			if (view == null)
			{
				MvxTrace.Warning("Received OnTouch-Event for root without a valid sender. OnTouch-Event possibly will be ignored.");
				return;
			}

			if (args?.Event == null)
			{
				MvxTrace.Error("Received OnTouch-Event for root without valid TouchEventArgs. OnTouch-Event will be ignored!");
				return;
			}

			var motionEvent = args.Event;
			switch (motionEvent.Action)
			{
				case MotionEventActions.Down:
					ToggleControlsVisibility();
					break;
				case MotionEventActions.Up:
					view.PerformClick();
					break;
			}
			args.Handled = true;
		}

		protected override void OnPause()
		{
			base.OnPause();
			UnregisterTouchAndClickEvents();
			ReleasePlayer();
			_shutterView.Visibility = ViewStates.Visible;
		}


		protected override void OnDestroy()
		{
			base.OnDestroy();
			_audioCapabilitiesReceiver.Unregister();
			ReleasePlayer();
		}

		#endregion

		#region AudioCapabilitiesReceiver.Listener methods

		public void OnAudioCapabilitiesChanged(AudioCapabilities audioCapabilities)
		{
			if (_player == null)
			{
				return;
			}
			var backgrounded = _player.Backgrounded;
			var playWhenReady = _player.PlayWhenReady;
			ReleasePlayer();
			PreparePlayer(playWhenReady);
			_player.Backgrounded = backgrounded;
		}

		#endregion

		#region Internal methods

		private MvxVideoPlayer.IRendererBuilder GetRendererBuilder()
		{
			var userAgent = Util.GetUserAgent(this, "ExoPlayerDemo");
			var url = Item.Url;
			switch (Item.Type)
			{
				case MvxVideoItem.ContentType.Hls:
					return new MvxHlsRendererBuilder(this, userAgent, url);
				case MvxVideoItem.ContentType.Other:
					return new MvxExtractorRendererBuilder(this, userAgent, Uri.Parse(url));
				default:
					throw new IllegalStateException("Unsupported type: " + Item.Type);
			}
		}

		private void PreparePlayer(bool playWhenReady)
		{
			if (_player == null)
			{
				_player = new MvxVideoPlayer(GetRendererBuilder());
				_player.AddListener(this);
				_player.SetCaptionListener(this);
				_player.SetMetadataListener(this);
				_player.SeekTo(_playerPosition);
				_playerNeedsPrepare = true;
				_mediaController.SetMediaPlayer(_player.PlayerControl);
				_mediaController.Enabled = true;
				_eventLogger = new MvxVideoPlayerEventLogger();
				_eventLogger.StartSession();
				_player.AddListener(_eventLogger);
				_player.SetInfoListener(_eventLogger);
				_player.SetInternalErrorListener(_eventLogger);
			}
			if (_playerNeedsPrepare)
			{
				_player.Prepare();
				_playerNeedsPrepare = false;
			}
			_player.Surface = _surfaceView.Holder.Surface;
			_player.PlayWhenReady = playWhenReady;
		}

		private void ReleasePlayer()
		{
			if (_player == null)
			{
				return;
			}

			_playerPosition = _player.CurrentPosition;
			_player.Release();
			_player = null;
			_eventLogger.EndSession();
			_eventLogger = null;
		}

		#endregion

		#region DemoPlayer.Listener implementation

		public void OnStateChanged(bool playWhenReady, int playbackState)
		{
			if (playbackState == Com.Google.Android.Exoplayer.ExoPlayer.StateEnded)
			{
				ShowControls();
			}
		}

		public void OnError(Exception e)
		{
			var exception = e as UnsupportedDrmException;
			if (exception != null)
			{
				// TODO
				// Special case DRM failures.
				var msg = Util.SdkInt < 18
					? "drm_error_not_supported"
					: exception.Reason == UnsupportedDrmException.ReasonUnsupportedScheme
						? "drm_error_unsupported_scheme"
						: "drm_error_unknown";
				Toast.MakeText(ApplicationContext, msg, ToastLength.Long).Show();
			}
			_playerNeedsPrepare = true;
			ShowControls();
		}

		public void OnVideoSizeChanged(
			int width,
			int height,
			int unappliedRotationDegrees,
			float pixelWidthAspectRatio)
		{
			_shutterView.Visibility = ViewStates.Gone;
			_videoFrame.SetAspectRatio(height == 0 ? 1 : (width*pixelWidthAspectRatio)/height);
		}

		#endregion

		#region User controls

		private void ToggleControlsVisibility()
		{
			if (_mediaController.IsShowing)
			{
				_mediaController.Hide();
			}
			else
			{
				ShowControls();
			}
		}

		private void ShowControls()
		{
			_mediaController.Show(0);
		}

		#endregion

		#region DemoPlayer.CaptionListener implementation

		public void OnCues(IList<Cue> cues)
		{
			_subtitleLayout.SetCues(cues);
		}

		#endregion

		#region DemoPlayer.MetadataListener implementation

		public void OnId3Metadata(object metadata)
		{
			/*for (Map.Entry<String, Object> entry : metadata.entrySet()) {
      if (TxxxMetadata.TYPE.equals(entry.getKey())) {
        TxxxMetadata txxxMetadata = (TxxxMetadata) entry.getValue();
        Log.i(TAG, String.format("ID3 TimedMetadata %s: description=%s, value=%s",
            TxxxMetadata.TYPE, txxxMetadata.description, txxxMetadata.value));
      } else if (PrivMetadata.TYPE.equals(entry.getKey())) {
        PrivMetadata privMetadata = (PrivMetadata) entry.getValue();
        Log.i(TAG, String.format("ID3 TimedMetadata %s: owner=%s",
            PrivMetadata.TYPE, privMetadata.owner));
      } else if (GeobMetadata.TYPE.equals(entry.getKey())) {
        GeobMetadata geobMetadata = (GeobMetadata) entry.getValue();
        Log.i(TAG, String.format("ID3 TimedMetadata %s: mimeType=%s, filename=%s, description=%s",
            GeobMetadata.TYPE, geobMetadata.mimeType, geobMetadata.filename,
            geobMetadata.description));
      } else {
        Log.i(TAG, String.format("ID3 TimedMetadata %s", entry.getKey()));
      }
    }*/
		}

		#endregion

		#region SurfaceHolder.Callback implementation

		public void SurfaceCreated(ISurfaceHolder holder)
		{
			if (_player != null)
			{
				_player.Surface = holder.Surface;
			}
		}

		public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
		{
			// Do nothing.
		}

		public void SurfaceDestroyed(ISurfaceHolder holder)
		{
			if (_player != null)
			{
				_player.BlockingClearSurface();
			}
		}

		#endregion

		private void ConfigureSubtitleView()
		{
			CaptionStyleCompat style;
			float fontScale;
			if (Util.SdkInt >= 19)
			{
				style = GetUserCaptionStyleV19();
				fontScale = GetUserCaptionFontScaleV19();
			}
			else
			{
				style = CaptionStyleCompat.Default;
				fontScale = 1.0f;
			}
			_subtitleLayout.SetStyle(style);
			_subtitleLayout.SetFractionalTextSize(SubtitleLayout.DefaultTextSizeFraction*fontScale);
		}

		private float GetUserCaptionFontScaleV19()
		{
			var captioningManager = (CaptioningManager) GetSystemService(CaptioningService);
			return captioningManager.FontScale;
		}

		private CaptionStyleCompat GetUserCaptionStyleV19()
		{
			var captioningManager = (CaptioningManager) GetSystemService(CaptioningService);
			return CaptionStyleCompat.CreateFromCaptionStyle(captioningManager.UserStyle);
		}
	}
}