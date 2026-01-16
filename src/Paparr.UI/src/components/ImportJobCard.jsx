import './ImportJobCard.css'

function ImportJobCard({ job, onAcceptCandidate, onRetry }) {
  const getStatusColor = (status) => {
    switch (status) {
      case 'AwaitingApproval':
        return '#f39c12'
      case 'Processing':
        return '#3498db'
      case 'Completed':
        return '#27ae60'
      case 'Failed':
        return '#e74c3c'
      case 'Pending':
        return '#95a5a6'
      default:
        return '#95a5a6'
    }
  }

  return (
    <div className="job-card">
      <div className="card-header">
        <div className="card-title">
          <span
            className="status-badge"
            style={{ backgroundColor: getStatusColor(job.status) }}
          >
            {job.status}
          </span>
        </div>
        <div className="card-meta">
          <small>{new Date(job.createdAt).toLocaleDateString()}</small>
        </div>
      </div>

      <div className="card-body">
        <div className="file-info">
          <strong>File:</strong>
          <p>{job.filePath.split('/').pop()}</p>
        </div>

        {job.status === 'AwaitingApproval' && job.candidates?.length > 0 && (
          <div className="candidates-section">
            <h4>Metadata Candidates</h4>
            <div className="candidates-list">
              {job.candidates.map(candidate => (
                <div key={candidate.id} className="candidate-item">
                  <div className="candidate-info">
                    <p className="candidate-title">{candidate.title}</p>
                    <p className="candidate-author">{candidate.author}</p>
                    <div className="candidate-meta">
                      <span className="source-badge">{candidate.source}</span>
                      <span className="confidence-badge">
                        {Math.round(candidate.confidenceScore)}%
                      </span>
                    </div>
                  </div>
                  <button
                    className="accept-btn"
                    onClick={() => onAcceptCandidate(job.id, candidate.id)}
                  >
                    Accept
                  </button>
                </div>
              ))}
            </div>
          </div>
        )}

        {job.status === 'Completed' && job.acceptedBook && (
          <div className="accepted-book">
            <h4>Accepted Book</h4>
            <p className="book-title">{job.acceptedBook.title}</p>
            <p className="book-author">{job.acceptedBook.author}</p>
            <p className="book-source">{job.acceptedBook.source}</p>
          </div>
        )}

        {job.status === 'Failed' && (
          <button
            className="retry-btn"
            onClick={() => onRetry(job.id)}
          >
            Retry Import
          </button>
        )}
      </div>
    </div>
  )
}

export default ImportJobCard
