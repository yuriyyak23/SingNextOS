using SingPlus.Contracts;

namespace SingPlus.Apps.Snake;

public static class SnakeBindings
{
    public const int Width = 16;
    public const int Height = 10;
    public static uint StepMessageId => ISnakeServiceProtocol.Message_Step;
    public static ProtocolDefinitionV1 Protocol => ISnakeServiceProtocol.CreateDefinition();
    public static ResponseProtocolDefinitionV1 Responses => ISnakeServiceResponseProtocol.Definition;
    public static ISnakeService CreateClient(ISipClientRuntimeTransport transport) => ISnakeServiceRuntimeClient.Create(transport);

    // The restricted child owns private game storage; this project has no RuntimeKernel reference.
    public static Func<SnakeMove, SnakeFrame> CreateDispatcher(Memory<int> storage)
    {
        var game = new Game(storage);
        return game.Dispatch;
    }

    internal sealed class Game : ISnakeService
    {
        private readonly Memory<int> _storage;
        private Span<int> _body => _storage.Span;
        private int _length;
        private int _direction;
        private int _food;
        private int _score;
        private int _status;

        public Game(Memory<int> storage)
        {
            if (storage.Length != Width * Height) throw new ArgumentException("Snake storage must contain 160 Int32 cells.", nameof(storage));
            _storage = storage;
            Reset();
        }

        public SnakeFrame Dispatch(SnakeMove move) => new ISnakeServiceDispatcher(this).Dispatch_Step(move);

        public SnakeFrame Step(SnakeMove request)
        {
            // 0 snapshot, 1 up, 2 right, 3 down, 4 left, 5 restart.
            if (request.Direction == 5) Reset();
            else if (request.Direction is >= 1 and <= 4 && _status == 0)
            {
                if (request.Direction != ((_direction + 1) % 4) + 1)
                    _direction = request.Direction;
                var head = _body[0];
                var x = head % Width;
                var y = head / Width;
                if (_direction == 1) y--;
                if (_direction == 2) x++;
                if (_direction == 3) y++;
                if (_direction == 4) x--;
                if (x < 0 || x >= Width || y < 0 || y >= Height) _status = 1;
                else
                {
                    var next = y * Width + x;
                    var grows = next == _food;
                    for (var i = 0; i < _length - (grows ? 0 : 1); i++)
                        if (_body[i] == next) _status = 1;
                    if (_status == 0)
                    {
                        if (grows) { _length++; _score++; }
                        for (var i = _length - 1; i > 0; i--) _body[i] = _body[i - 1];
                        _body[0] = next;
                        if (_length == _body.Length) { _status = 2; _food = -1; }
                        else if (grows) PlaceFood();
                    }
                }
            }
            ulong a = 0, b = 0, c = 0;
            for (var i = 0; i < _length; i++)
            {
                var cell = _body[i];
                if (cell < 64) a |= 1UL << cell;
                else if (cell < 128) b |= 1UL << (cell - 64);
                else c |= 1UL << (cell - 128);
            }
            return new SnakeFrame(a, b, c, _body[0], _food, _score, _status);
        }

        private void Reset()
        {
            _length = 3;
            _body[0] = 5 * Width + 5;
            _body[1] = _body[0] - 1;
            _body[2] = _body[0] - 2;
            _direction = 2;
            _food = _body[0] + 3;
            _score = 0;
            _status = 0;
        }

        private void PlaceFood()
        {
            // Deterministic placement makes gameplay and scripted demos reproducible.
            var start = (_body[0] + 17 + _score * 13) % _body.Length;
            for (var offset = 0; offset < _body.Length; offset++)
            {
                var candidate = (start + offset) % _body.Length;
                var occupied = false;
                for (var i = 0; i < _length; i++) if (_body[i] == candidate) occupied = true;
                if (!occupied) { _food = candidate; return; }
            }
        }
    }
}