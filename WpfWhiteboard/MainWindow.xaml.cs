using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WpfWhiteboard
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, IDisposable
	{
		private Socket _broadcastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		private Socket _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		private Guid _userId = Guid.NewGuid();

		private Dictionary<Guid, Polyline> _users = new Dictionary<Guid, Polyline>();

		private EndPoint _broadcastEndPoint = new IPEndPoint(IPAddress.Parse("192.168.0.255"), 50000);
		private EndPoint _listenEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 50000);

		private int _eraseDecisionAccepts = 0;
		private int _totalDecisionsMade = 0;
		private byte[] _buffer = new byte[34];


		public MainWindow()
		{
			InitializeComponent();
			this.Loaded += WindowLoaded;
		}

		private void WindowLoaded(object sender, RoutedEventArgs e)
		{
			//initialize _listenSocket and _broadcastsocket
			_listenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			_listenerSocket.Bind(_listenEndPoint);
			_listenerSocket.EnableBroadcast = true;
			_broadcastSocket.EnableBroadcast = true;
			
			//start processing loop on another thread
			Task task = new Task(ProcessMessage);
			task.Start();


			//automation

			//DispatcherTimer timer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(0.01) };
			//var random = new Random();
			//var prevX = 200.0;
			//var prevY = 200.0;
			//timer.Tick += (s, args) =>
			//{
			//	byte[] payload = PreparePayload(new Point
			//		(
			//		prevX = prevX + random.NextDouble() * 10 - 5,
			//		prevY = prevY + random.NextDouble() * 10 - 5
			//		),
			//		random.NextDouble() > 0.1 ? (byte)1 : (byte)0);
			//	if (prevX > 500 || prevX < 0)
			//		prevX = 200;
			//	if (prevY > 500 || prevY < 0)
			//		prevY = 200;
			//	_broadcastSocket.SendTo(payload, _broadcastEndPoint);
			//};
			//timer.Start();

		}



		private async void ProcessMessage()
		{
			while (true)
			{
				int bytesTransferred = _listenerSocket.ReceiveFrom(_buffer, ref _listenEndPoint);

				switch (bytesTransferred)
				{
					case 33:
						await ProcessDrawMessage();
						break;
					case 17:
						ProcessEraseMessage();
						break;
					default:
						break;
				}
			}
		}


		private async void ProcessEraseMessage()
		{
			var guidBytes = _buffer.Take(16).ToArray();
			var guid = new Guid(guidBytes);
			var decision = _buffer[16];

			_eraseDecisionAccepts += decision;
			_totalDecisionsMade += 1;
			await Dispatcher.InvokeAsync(() =>
			{
				DecisionProgress.Value = _eraseDecisionAccepts / (double)_totalDecisionsMade;
			});

			if (decision == 1 && _eraseDecisionAccepts == 1)
			{
				await Dispatcher.InvokeAsync(() =>
				{
					if (guid != _userId) //if it isn't the initiator, show decision ui
					{
						DecisionGroup.Visibility = Visibility.Visible;
						OptionsPane.Visibility = Visibility.Visible;
					}
					else //else show the progress
						DecisionProgressGroup.Visibility = Visibility.Visible;

					EraseButton.Visibility = Visibility.Collapsed;
				});
				await Task.Delay(5000); //wait 5 seconds for answer
				await Dispatcher.InvokeAsync(() =>
				{
					if (_eraseDecisionAccepts > _totalDecisionsMade / 2)
						Whiteboard.Children.Clear();
					_eraseDecisionAccepts = 0;
					_totalDecisionsMade = 0;
					EraseButton.Visibility = Visibility.Visible;
					DecisionGroup.Visibility = Visibility.Collapsed;
					OptionsPane.Visibility = Visibility.Collapsed;
					DecisionProgressGroup.Visibility = Visibility.Collapsed;
					EraseButton.IsEnabled = true;
				});
			}
		}

		private async Task ProcessDrawMessage()
		{
			var guidBytes = _buffer.Take(16).ToArray();
			var guid = new Guid(guidBytes);


			var x = BitConverter.ToDouble(_buffer, 16);
			var y = BitConverter.ToDouble(_buffer, 24);
			var continueLine = _buffer[32];

			if (!_users.ContainsKey(guid))
			{
				await Dispatcher.InvokeAsync(() =>
				{
					StartNewLine(guid);
				});
			}

			if (continueLine == 1)
			{
				await Dispatcher.InvokeAsync(() =>
				{
					_users[guid].Points.Add(new Point(x, y));
				});
			}
			else
			{
				await Dispatcher.InvokeAsync(() =>
				{
					StartNewLine(guid);
				});
			}
		}


		private void StartNewLine(Guid userId)
		{
			//create new line
			var polyline = new Polyline { Stroke = new SolidColorBrush(GenerateColor(userId.ToByteArray())), StrokeThickness = 1 };
			Whiteboard.Children.Add(polyline);
			//assign line to user
			_users[userId] = polyline;
			

			//bring line on top of the canvas
			if (Whiteboard.Children.OfType<FrameworkElement>().Count() < 2)
				return;
			var maxZ = Whiteboard.Children.OfType<FrameworkElement>()
			.Where(line => line != polyline)
			.Select(line => Panel.GetZIndex(line))
			.Max();
			Panel.SetZIndex(polyline, maxZ + 1);
		}

		private Color GenerateColor(byte[] bytes)
		{
			var r = BitConverter.ToInt32(bytes, 0);
			var g = BitConverter.ToInt32(bytes, 4);
			var b = BitConverter.ToInt32(bytes, 12);
			return Color.FromArgb(255, (byte)(r % 255), (byte)(g % 255), (byte)(b % 255));
		}

		public void Dispose()
		{
			_listenerSocket.Dispose();
			_broadcastSocket.Dispose();
		}

		private byte[] PreparePayload(Point position, byte continueLine)
		{
			byte[] payload = new byte[33];
			byte[] guid = _userId.ToByteArray();

			//first 16 bytes is the guid of user
			for (int i = 0; i < 16; i++)
				payload[i] = guid[i];

			byte[] x = BitConverter.GetBytes(position.X);
			byte[] y = BitConverter.GetBytes(position.Y);

			//next 8 is the double position on x
			for (int i = 0; i < 8; i++)
				payload[i + 16] = x[i];
			//next 8 is the double position on y
			for (int i = 0; i < 8; i++)
				payload[i + 24] = y[i];
			//whether to continue the line or start new
			payload[32] = continueLine;
			return payload;
		}

		private byte[] PreparePayload(byte decision)
		{
			byte[] payload = new byte[17];
			byte[] guid = _userId.ToByteArray();

			//first 16 bytes is the guid of user
			for (int i = 0; i < 16; i++)
				payload[i] = guid[i];
			//decision the user made
			payload[16] = decision;
			return payload;
		}


		private void Whiteboard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			_broadcastSocket.SendTo(PreparePayload(e.GetPosition(sender as Canvas), 0), _broadcastEndPoint);
		}

		private void Whiteboard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
				_broadcastSocket.SendTo(PreparePayload(e.GetPosition(sender as Canvas), 1), _broadcastEndPoint);
		}

		private void Whiteboard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
				_broadcastSocket.SendTo(PreparePayload(e.GetPosition(sender as Canvas), 1), _broadcastEndPoint);
		}
		private void Whiteboard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
				_broadcastSocket.SendTo(PreparePayload(e.GetPosition(sender as Canvas), 1), _broadcastEndPoint);
		}

		private void Whiteboard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
				_broadcastSocket.SendTo(PreparePayload(e.GetPosition(sender as Canvas), 0), _broadcastEndPoint);
		}

		private void OptionsButtonClicked(object sender, RoutedEventArgs e)
		{
			OptionsPane.Visibility = OptionsPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible ;
		}

		private void EraseButtonClick(object sender, RoutedEventArgs e)
		{
			_broadcastSocket.SendTo(PreparePayload(1), _broadcastEndPoint);
		}

		private void AcceptEraseButtonClick(object sender, RoutedEventArgs e)
		{
			_broadcastSocket.SendTo(PreparePayload(1), _broadcastEndPoint);
			DecisionGroup.Visibility = Visibility.Collapsed;
			EraseButton.Visibility = Visibility.Collapsed;
			DecisionProgressGroup.Visibility = Visibility.Visible;

		}

		private void RefuseEraseButtonClick(object sender, RoutedEventArgs e)
		{
			_broadcastSocket.SendTo(PreparePayload(0), _broadcastEndPoint);
			DecisionGroup.Visibility = Visibility.Collapsed;
			EraseButton.Visibility = Visibility.Collapsed;
			DecisionProgressGroup.Visibility = Visibility.Visible;
		}
	}
}
