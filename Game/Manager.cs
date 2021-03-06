using Celeste;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using System;
using System.Globalization;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
namespace TAS {
	[Flags]
	public enum State {
		None = 0,
		Enable = 1,
		Record = 2,
		FrameStep = 4,
		Disable = 8
	}
	public class Manager {
		public static bool Running, Recording;
		private static InputController controller = new InputController("Celeste.tas");
		public static State state, nextState;
		public static string CurrentStatus, PlayerStatus;
		public static int FrameStepCooldown, FrameLoops = 1;
		private static bool frameStepWasDpadUp, frameStepWasDpadDown;
		private static Vector2 lastPos;
		private static long lastTimer;
		private static CultureInfo enUS = CultureInfo.CreateSpecificCulture("en-US");
		private static KeyboardState kbState;
		private static List<VirtualButton.Node>[] playerBindings;
		private static KeyBindings bindings;
		private static bool keysInitialized;

		private static bool IsKeyDown(List<Keys> keys) {
			foreach (Keys key in keys) {
				if (!kbState.IsKeyDown(key))
					return false;
			}
			return true;
		}
		public static bool IsLoading() {
			if (Engine.Scene is SummitVignette summit) {
				return !summit.ready;
			} else if (Engine.Scene is Overworld overworld) {
				return overworld.Current is OuiFileSelect slot && slot.SlotIndex >= 0 && slot.Slots[slot.SlotIndex].StartingGame;
			}
			return (Engine.Scene is LevelExit) || (Engine.Scene is LevelLoader) || (Engine.Scene is GameLoader);
		}
		private static GamePadState GetGamePadState() {
			GamePadState padState = MInput.GamePads[0].CurrentState;
			for (int i = 0; i < 4; i++) {
				padState = GamePad.GetState((PlayerIndex)i);
				if (padState.IsConnected) {
					break;
				}
			}
			return padState;
		}
		private static void UpdatePlayerInfo() {
			Player player = null;
			long chapterTime = 0;
			if (Engine.Scene is Level level) {
				player = level.Tracker.GetEntity<Player>();
				if (player != null) {
					chapterTime = level.Session.Time;
					if (chapterTime != lastTimer || lastPos != player.ExactPosition) {
						string pos = $"Pos: {player.ExactPosition.X.ToString("0.00", enUS)},{player.ExactPosition.Y.ToString("0.00", enUS)}";
						string speed = $"Speed: {player.Speed.X.ToString("0.00", enUS)},{player.Speed.Y.ToString("0.00", enUS)}";
						Vector2 diff = (player.ExactPosition - lastPos) * 60;
						string vel = $"Vel: {diff.X.ToString("0.00", enUS)},{diff.Y.ToString("0.00", enUS)}";
						string polarvel = $"     {diff.Length().ToString("0.00", enUS)},{GetAngle(diff).ToString("0.00", enUS)}°";
						string miscstats = $"Stamina: {player.Stamina.ToString("0")} Timer: {(chapterTime / 10000000D).ToString("0.000", enUS)}";
						string statuses = ((int)(player.dashCooldownTimer * 60f) < 1 && player.Dashes > 0 ? "Dash " : string.Empty) + (player.LoseShards ? "Ground " : string.Empty) + (player.WallJumpCheck(1) ? "Wall-R " : string.Empty) + (player.WallJumpCheck(-1) ? "Wall-L " : string.Empty) + (!player.LoseShards && player.jumpGraceTimer > 0 ? "Coyote " : string.Empty);
						statuses = ((player.InControl && !level.Transitioning ? statuses : "NoControl ") + (player.TimePaused ? "Paused " : string.Empty) + (level.InCutscene ? "Cutscene " : string.Empty));
						if (player.Holding == null) {
							foreach (Component component in level.Tracker.GetComponents<Holdable>()) {
								Holdable holdable = (Holdable)component;
								if (holdable.Check(player)) {
									statuses += "Grab ";
									break;
								}
							}
						}

						int berryTimer = -10;
						Follower firstRedBerryFollower = player.Leader.Followers.Find(follower => follower.Entity is Strawberry berry && !berry.Golden);
						if (firstRedBerryFollower?.Entity is Strawberry firstRedBerry) {
							berryTimer = (int)Math.Round(60f * firstRedBerry.collectTimer);
						}
						string timers = (berryTimer != -10 ? $"BerryTimer: {berryTimer.ToString()} " : string.Empty) + ((int)(player.dashCooldownTimer * 60f) != 0 ? $"DashTimer: {((int)Math.Round(player.dashCooldownTimer * 60f) - 1).ToString()} " : string.Empty);

						StringBuilder sb = new StringBuilder();
						sb.AppendLine(pos);
						sb.AppendLine(speed);
						sb.AppendLine(vel);
						if (player.StateMachine.State == 19 || SaveData.Instance.Assists.ThreeSixtyDashing || SaveData.Instance.Assists.SuperDashing) {
							sb.AppendLine(polarvel);
						}
						sb.AppendLine(miscstats);
						if (!string.IsNullOrEmpty(statuses)) {
							sb.AppendLine(statuses);
						}
						sb.Append(timers);
						PlayerStatus = sb.ToString().TrimEnd();
						lastPos = player.ExactPosition;
						lastTimer = chapterTime;
					}
				} else {
					PlayerStatus = level.InCutscene ? "Cutscene" : null;
				}
			} else if (Engine.Scene is SummitVignette summit) {
				PlayerStatus = string.Concat("SummitVignette ", summit.ready);
			} else if (Engine.Scene is Overworld overworld) {
				PlayerStatus = string.Concat("Overworld ", overworld.ShowInputUI);
			} else if (Engine.Scene != null) {
				PlayerStatus = Engine.Scene.GetType().Name;
			}
		}
		public static float GetAngle(Vector2 vector) {
			float angle = 360f / 6.283186f * Calc.Angle(vector);
			if (angle < -90.01f) {
				return 450f + angle;
			} else {
				return 90f + angle;
			}
		}
		public static void UpdateInputs() {
			if (!keysInitialized)
				InitializeKeys();
			UpdatePlayerInfo();
			kbState = Keyboard.GetState();
			GamePadState padState = GetGamePadState();
			HandleFrameRates(padState);
			CheckControls(padState);
			FrameStepping(padState);

			if (HasFlag(state, State.Enable)) {
				Running = true;

				if (HasFlag(state, State.FrameStep)) {
					return;
				}

				if (HasFlag(state, State.Record)) {
					controller.RecordPlayer();
				} else {
					bool fastForward = controller.HasFastForward;
					controller.PlaybackPlayer();
					if (fastForward
						&& (!controller.HasFastForward
							|| controller.Current.ForceBreak && controller.CurrentInputFrame == controller.Current.Frames)) {
						nextState |= State.FrameStep;
						FrameLoops = 1;
					}

					if (!controller.CanPlayback) {
						DisableRun();
					}
				}
				string status = controller.Current.Line + "[" + controller.ToString() + "]";
				CurrentStatus = status;
			} else {
				Running = false;
				CurrentStatus = null;

				if (!Engine.Instance.IsActive) {
					for (int i = 0; i < 4; i++) {
						if (MInput.GamePads[i].Attached) {
							MInput.GamePads[i].CurrentState = padState;
						}
					}
					MInput.UpdateVirtualInputs();
				}
			}
		}
		private static void HandleFrameRates(GamePadState padState) {
			if (HasFlag(state, State.Enable) && !HasFlag(state, State.FrameStep) && !HasFlag(nextState, State.FrameStep) && !HasFlag(state, State.Record)) {
				if (controller.HasFastForward) {
					FrameLoops = controller.FastForwardSpeed;
					return;
				}

				float rightStickX = padState.ThumbSticks.Right.X;
				if (IsKeyDown(bindings.keyFastForward))
					rightStickX = 1f;
				if (rightStickX <= 0.2)
					FrameLoops = 1;
				else
					FrameLoops = (int)(10 * rightStickX);
			} else {
				FrameLoops = 1;
			}
		}
		private static void FrameStepping(GamePadState padState) {
			bool rightTrigger = padState.Triggers.Right > 0.5f;
			bool dpadUp = padState.DPad.Up == ButtonState.Pressed || (IsKeyDown(bindings.keyFrameAdvance) && !IsKeyDown(bindings.keyStart));
			bool dpadDown = padState.DPad.Down == ButtonState.Pressed || (IsKeyDown(bindings.keyPause) && !IsKeyDown(bindings.keyStart));

			if (HasFlag(state, State.Enable) && !HasFlag(state, State.Record) && !rightTrigger) {
				if (HasFlag(nextState, State.FrameStep)) {
					state |= State.FrameStep;
					nextState &= ~State.FrameStep;
				}

				if (!dpadUp && frameStepWasDpadUp) {
					if (!HasFlag(state, State.FrameStep)) {
						state |= State.FrameStep;
						nextState &= ~State.FrameStep;
					} else {
						state &= ~State.FrameStep;
						nextState |= State.FrameStep;
						controller.ReloadPlayback();
					}
					FrameStepCooldown = 60;
				} else if (!dpadDown && frameStepWasDpadDown) {
					state &= ~State.FrameStep;
					nextState &= ~State.FrameStep;
				} else if (HasFlag(state, State.FrameStep) && (padState.ThumbSticks.Right.X > 0.1 || IsKeyDown(bindings.keyFastForward))) {
					float rStick = padState.ThumbSticks.Right.X;
					if (rStick < 0.1f) {
						rStick = 0.5f;
					}
					FrameStepCooldown -= (int)((rStick - 0.1) * 80f);
					if (FrameStepCooldown <= 0) {
						FrameStepCooldown = 60;
						state &= ~State.FrameStep;
						nextState |= State.FrameStep;
						controller.ReloadPlayback();
					}
				}
			}

			frameStepWasDpadUp = dpadUp;
			frameStepWasDpadDown = dpadDown;
		}
		private static void CheckControls(GamePadState padState) {
			bool openBracket = IsKeyDown(bindings.keyStart);
			bool rightStick = padState.Buttons.RightStick == ButtonState.Pressed || openBracket;

			if (rightStick) {
				if (!HasFlag(state, State.Enable)) {
					nextState |= State.Enable;
				} else {
					nextState |= State.Disable;
				}
			} else if (HasFlag(nextState, State.Enable)) {
				EnableRun();
			} else if (HasFlag(nextState, State.Disable)) {
				DisableRun();
			}
		}
		private static void DisableRun() {
			Running = false;
			if (Recording) {
				controller.WriteInputs();
			}
			Recording = false;
			state = State.None;
			nextState = State.None;
			RestorePlayerBindings();
		}
		private static void EnableRun() {
			nextState &= ~State.Enable;
			UpdateVariables(false);
			BackupPlayerBindings();
		}
		private static void BackupPlayerBindings() {
			playerBindings = new List<VirtualButton.Node>[5] { Input.Jump.Nodes, Input.Dash.Nodes, Input.Grab.Nodes, Input.Talk.Nodes, Input.QuickRestart.Nodes};
			Input.Jump.Nodes = new List<VirtualButton.Node> { new VirtualButton.PadButton(Input.Gamepad, Buttons.A), new VirtualButton.PadButton(Input.Gamepad, Buttons.Y) };
			Input.Dash.Nodes = new List<VirtualButton.Node> { new VirtualButton.PadButton(Input.Gamepad, Buttons.B), new VirtualButton.PadButton(Input.Gamepad, Buttons.X) };
			Input.Grab.Nodes = new List<VirtualButton.Node> { new VirtualButton.PadButton(Input.Gamepad, Buttons.RightShoulder) };
			Input.Talk.Nodes = new List<VirtualButton.Node> { new VirtualButton.PadButton(Input.Gamepad, Buttons.B) };
			Input.QuickRestart.Nodes = new List<VirtualButton.Node> { new VirtualButton.PadButton(Input.Gamepad, Buttons.LeftShoulder) };
		}
		private static void RestorePlayerBindings() {
			Input.Jump.Nodes = playerBindings[0];
			Input.Dash.Nodes = playerBindings[1];
			Input.Grab.Nodes = playerBindings[2];
			Input.Talk.Nodes = playerBindings[3];
			Input.QuickRestart.Nodes = playerBindings[4];
		}
		private static void UpdateVariables(bool recording) {
			state |= State.Enable;
			state &= ~State.FrameStep;
			if (recording) {
				Recording = recording;
				state |= State.Record;
				controller.InitializeRecording();
			} else {
				state &= ~State.Record;
				controller.InitializePlayback();
			}
			Running = true;
		}
		private static bool HasFlag(State state, State flag) {
			return (state & flag) == flag;
		}
		public static void SetInputs(InputRecord input) {
			GamePadDPad pad;
			GamePadThumbSticks sticks;
			if (input.HasActions(Actions.Feather)) {
				pad = new GamePadDPad(ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
				sticks = new GamePadThumbSticks(new Vector2(input.GetX(), input.GetY()), new Vector2(0, 0));
			} else {
				pad = new GamePadDPad(
					input.HasActions(Actions.Up) ? ButtonState.Pressed : ButtonState.Released,
					input.HasActions(Actions.Down) ? ButtonState.Pressed : ButtonState.Released,
					input.HasActions(Actions.Left) ? ButtonState.Pressed : ButtonState.Released,
					input.HasActions(Actions.Right) ? ButtonState.Pressed : ButtonState.Released
				);
				sticks = new GamePadThumbSticks(new Vector2(0, 0), new Vector2(0, 0));
			}
			GamePadState state = new GamePadState(
				sticks,
				new GamePadTriggers(input.HasActions(Actions.Journal) ? 1f : 0f, 0),
				new GamePadButtons(
					(input.HasActions(Actions.Jump) ? Buttons.A : 0)
					| (input.HasActions(Actions.Jump2) ? Buttons.Y : 0)
					| (input.HasActions(Actions.Dash) ? Buttons.B : 0)
					| (input.HasActions(Actions.Dash2) ? Buttons.X : 0)
					| (input.HasActions(Actions.Grab) ? Buttons.RightShoulder : 0)
					| (input.HasActions(Actions.Start) ? Buttons.Start : 0)
					| (input.HasActions(Actions.Restart) ? Buttons.LeftShoulder : 0)
				),
				pad
			);

			bool found = false;
			for (int i = 0; i < 4; i++) {
				MInput.GamePads[i].Update();
				if (MInput.GamePads[i].Attached) {
					found = true;
					MInput.GamePads[i].CurrentState = state;
				}
			}

			if (!found) {
				MInput.GamePads[0].CurrentState = state;
				MInput.GamePads[0].Attached = true;
			}

			if (input.HasActions(Actions.Confirm)) {
				MInput.Keyboard.CurrentState = new KeyboardState(Keys.Enter);
			} else {
				MInput.Keyboard.CurrentState = new KeyboardState();
			}

			MInput.UpdateVirtualInputs();
		}
		private static void InitializeKeys() {
			string filePath = Directory.GetCurrentDirectory() + "\\TASsettings.xml";
			try {
				using (FileStream fs = File.OpenRead(filePath)) {
					XmlSerializer xml = new XmlSerializer(typeof(KeyBindings));
					bindings = (KeyBindings)xml.Deserialize(fs);
				}
			}
			catch {
				bindings.keyStart = new List<Keys> { Keys.RightControl, Keys.OemOpenBrackets };
				bindings.keyFastForward = new List<Keys> { Keys.RightControl, Keys.RightShift };
				bindings.keyFrameAdvance = new List<Keys> { Keys.OemOpenBrackets };
				bindings.keyPause = new List<Keys> { Keys.OemCloseBrackets };
				using (FileStream fs = File.Create(filePath)) {
					XmlSerializer xml = new XmlSerializer(typeof(KeyBindings));
					xml.Serialize(fs, bindings);
				}
				InitializeKeys();
			}

			keysInitialized = true;
		}
	}
}
