#nullable enable
using System;
using System.Collections.Generic;

namespace Arena.Input
{
    /// <summary>
    /// Shared bounded history of locally authored movement commands.
    /// Commands are appended in tick order, consumed for local replay, and
    /// sent/pruned separately by the transport layer.
    /// </summary>
    public sealed class MovementCommandBuffer
    {
        private readonly MovementCommand[] _commands;
        private int _head;
        private int _count;
        private int _sentCount;
        private uint _nextInputTick = 1;
        private uint _generation;

        public MovementCommandBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _commands = new MovementCommand[capacity];
        }

        public int Count => _count;
        public int Capacity => _commands.Length;
        public int UnsentCount => _count - _sentCount;
        public uint NextInputTick => _nextInputTick;
        public uint Generation => _generation;
        public uint? OldestPendingTick => _count > 0 ? _commands[_head].InputTick : null;
        public uint? NewestPendingTick => _count > 0 ? _commands[(_head + _count - 1) % _commands.Length].InputTick : null;
        public uint? OldestUnsentTick => _sentCount < _count ? _commands[(_head + _sentCount) % _commands.Length].InputTick : null;

        public void Reset(uint nextInputTick = 1)
        {
            _head = 0;
            _count = 0;
            _sentCount = 0;
            _nextInputTick = nextInputTick;
            _generation++;
        }

        public MovementCommand AppendNext(in LocalPlayerMotor.IntentSample intent)
        {
            MovementCommand command = new(
                _nextInputTick++,
                intent.Forward,
                intent.Strafe,
                intent.Yaw,
                intent.Jump);
            Add(command);
            return command;
        }

        public void Add(in MovementCommand command)
        {
            if (_count == _commands.Length)
            {
                _commands[_head] = command;
                _head = (_head + 1) % _commands.Length;
                if (_sentCount > 0)
                    _sentCount--;
                return;
            }

            int writeIndex = (_head + _count) % _commands.Length;
            _commands[writeIndex] = command;
            _count++;
        }

        public void PruneUpTo(uint lastProcessedTick)
        {
            int removed = 0;
            while (_count > 0 && _commands[_head].InputTick <= lastProcessedTick)
            {
                _head = (_head + 1) % _commands.Length;
                _count--;
                removed++;
            }

            if (removed > 0)
                _sentCount = Math.Max(0, _sentCount - removed);

            uint nextTick = lastProcessedTick == uint.MaxValue ? uint.MaxValue : lastProcessedTick + 1;
            if (_nextInputTick < nextTick)
                _nextInputTick = nextTick;
        }

        public void CopyPendingTo(List<MovementCommand> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            destination.Clear();
            if (destination.Capacity < _count)
                destination.Capacity = _count;

            for (int i = 0; i < _count; i++)
                destination.Add(_commands[(_head + i) % _commands.Length]);
        }

        public void CopyUnsentTo(List<MovementCommand> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            destination.Clear();
            int unsentCount = UnsentCount;
            if (destination.Capacity < unsentCount)
                destination.Capacity = unsentCount;

            for (int i = _sentCount; i < _count; i++)
                destination.Add(_commands[(_head + i) % _commands.Length]);
        }

        public void MarkSent(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            _sentCount = Math.Min(_count, _sentCount + count);
        }
    }
}
