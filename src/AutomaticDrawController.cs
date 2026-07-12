using System;

namespace DuelLinksAccess
{
    public sealed class AutomaticDrawController
    {
        private bool _attempted;
        private bool _gesturePending;
        private bool _detailPending;

        public bool Update(
            bool duelActive,
            bool localTurn,
            bool drawPhase,
            Action dispatch)
        {
            if (!duelActive || !localTurn)
            {
                _attempted = false;
                _gesturePending = false;
                _detailPending = false;
                return false;
            }

            if (!drawPhase)
            {
                _attempted = false;
                return false;
            }

            if (_attempted) return false;

            return Dispatch(dispatch);
        }

        public bool Retry(
            bool duelActive,
            bool localTurn,
            bool drawPhase,
            Action dispatch)
        {
            if (!duelActive || !localTurn || !drawPhase) return false;

            return Dispatch(dispatch);
        }

        public bool CompleteGesture(bool operationReady, Action complete)
        {
            if (!_gesturePending || !operationReady) return false;
            if (complete == null) throw new ArgumentNullException(nameof(complete));

            _gesturePending = false;
            try
            {
                complete();
                return true;
            }
            catch
            {
                _gesturePending = true;
                throw;
            }
        }

        public bool CompleteDetail(bool operationReady, Action complete)
        {
            if (!_detailPending || !operationReady) return false;
            if (complete == null) throw new ArgumentNullException(nameof(complete));

            _detailPending = false;
            try
            {
                complete();
                return true;
            }
            catch
            {
                _detailPending = true;
                throw;
            }
        }

        public void Reset()
        {
            _attempted = false;
            _gesturePending = false;
            _detailPending = false;
        }

        private bool Dispatch(Action dispatch)
        {
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));

            _attempted = true;
            try
            {
                dispatch();
                _gesturePending = true;
                _detailPending = true;
                return true;
            }
            catch
            {
                _attempted = false;
                throw;
            }
        }
    }
}
